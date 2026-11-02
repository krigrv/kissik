// GPU twin of the CPU frame path (BackgroundEffects, ImageFilters, SceneCompositor, MaskOps.Upsample).
// Every kernel mirrors its C# counterpart step by step, including the byte rounding between steps,
// so the two paths agree to within a level or two. Pixels are packed BGRA: B in the low byte.

cbuffer Params : register(b0)
{
    int4 I0;   // w, h, maskW, maskH
    int4 I1;   // blurW, blurH, blurRadius, mode (0 none, 1 blur, 2 replace, 3 remove)
    int4 I2;   // flags, filter, lutSize, 0
    int4 I3;   // camera rect x, y, w, h; during edge refinement: guide w, h, scale, radius
    int4 I4;   // scene flags
    float4 F0; // spotlight, retouch, lutStrength, sharpness
    float4 F1; // temperature, tint, saturation, vibrance
    float4 F2; // hue matrix m00 m01 m02 m10
    float4 F3; // m11 m12 m20 m21
    float4 F4; // m22, camera opacity, corner radius (px), 0
    float4 F5; // scene background top (b, g, r)
    float4 F6; // scene background bottom (b, g, r)
    float4 F7; // LUT domain min (r, g, b)
    float4 F8; // LUT domain max (r, g, b)
    float4 F9; // camera fit: left, top, scale, 0
};

// flags
#define F_TEMP 1
#define F_TINT 2
#define F_HUE 4
#define F_VIB 8
#define F_SAT 16
#define F_LUT 32
#define F_COLOUR 64
#define F_BEHIND 128
#define F_SPOT 256
// scene flags
#define S_FILLS 1
#define S_ELLIPSE 2
#define S_CAMERA 4
#define S_OVERLAY 8
#define S_UNDERLAY 16

StructuredBuffer<uint> Src : register(t0);
StructuredBuffer<float> Alpha : register(t1);
StructuredBuffer<uint> Aux : register(t2);     // replacement background, or the scene overlay
StructuredBuffer<uint> Behind : register(t3);
StructuredBuffer<float> Tone : register(t4);
StructuredBuffer<float> Lut : register(t5);
StructuredBuffer<float> MaskLow : register(t6);
StructuredBuffer<float4> Acc : register(t7);

RWStructuredBuffer<uint> Dst : register(u0);
RWStructuredBuffer<float> AlphaOut : register(u1);
RWStructuredBuffer<float4> AccOut : register(u2);

#define LIGHT_WRAP 0.35f // BackgroundEffects.LightWrap
#define W I0.x
#define H I0.y

float4 Unpack(uint p) { return float4(p & 255, (p >> 8) & 255, (p >> 16) & 255, p >> 24); }
uint Pack(uint4 c) { return c.x | (c.y << 8) | (c.z << 16) | (c.w << 24); }
uint Sat(float v) { return (uint)clamp(v, 0, 255); }           // C# (byte)Math.Clamp: truncates
uint3 Sat3(float3 v) { return uint3(Sat(v.x), Sat(v.y), Sat(v.z)); }
uint ToByte(float v) { return (uint)clamp(round(v * 255), 0, 255); }

uint Index(uint3 id) { return id.x + id.y * 65535u * 256u; }

[numthreads(256, 1, 1)]
void Upsample(uint3 id : SV_DispatchThreadID)
{
    uint p = Index(id);
    if (p >= (uint)(W * H)) return;
    int x = p % W, y = p / W, mw = I0.z, mh = I0.w;
    float sx = (float)mw / W, sy = (float)mh / H;
    float fy = clamp((y + 0.5f) * sy - 0.5f, 0, mh - 1);
    int y0 = (int)fy, y1 = min(y0 + 1, mh - 1); float dy = fy - y0;
    float fx = clamp((x + 0.5f) * sx - 0.5f, 0, mw - 1);
    int x0 = (int)fx, x1 = min(x0 + 1, mw - 1); float dx = fx - x0;
    float a = MaskLow[y0 * mw + x0] * (1 - dx) + MaskLow[y0 * mw + x1] * dx;
    float b = MaskLow[y1 * mw + x0] * (1 - dx) + MaskLow[y1 * mw + x1] * dx;
    float v = a * (1 - dy) + b * dy;
    v = clamp((v - 0.25f) / 0.5f, 0, 1);
    AlphaOut[p] = v * v * (3 - 2 * v);
}

// MaskOps.GuidedRefine: a fast guided filter that snaps the upsampled mask to edges in the picture.
#define GW I3.x
#define GH I3.y
float Lum(uint p) { float4 c = Unpack(p); return (c.x * 0.114f + c.y * 0.587f + c.z * 0.299f) / 255; }

// Guide image and mask at 1/scale: (I, p, I·p, I²).
[numthreads(256, 1, 1)]
void GuideDown(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    if (q >= (uint)(GW * GH)) return;
    int x = q % GW, y = q / GW, s = I3.z;
    int sy = min(H - 1, y * s + s / 2), sx = min(W - 1, x * s + s / 2);
    float i = Lum(Src[sy * W + sx]), p = Alpha[sy * W + sx];
    AccOut[q] = float4(i, p, i * p, i * i);
}

// Mean over the part of the (2r+1) window inside the image, one direction at a time.
[numthreads(256, 1, 1)]
void GuideBoxH(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    if (q >= (uint)(GW * GH)) return;
    int x = q % GW, row = q - x, a = max(0, x - I3.w), b = min(GW - 1, x + I3.w);
    float4 sum = 0;
    for (int k = a; k <= b; k++) sum += Acc[row + k];
    AccOut[q] = sum / (b - a + 1);
}

[numthreads(256, 1, 1)]
void GuideBoxV(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    if (q >= (uint)(GW * GH)) return;
    int x = q % GW, y = q / GW, a = max(0, y - I3.w), b = min(GH - 1, y + I3.w);
    float4 sum = 0;
    for (int k = a; k <= b; k++) sum += Acc[k * GW + x];
    AccOut[q] = sum / (b - a + 1);
}

// Per-window linear model alpha ≈ a·I + b.
[numthreads(256, 1, 1)]
void GuideAB(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    if (q >= (uint)(GW * GH)) return;
    float4 m = Acc[q];
    float cov = m.z - m.x * m.y, vr = m.w - m.x * m.x;
    float a = cov / (vr + 0.002f);
    AccOut[q] = float4(a, m.y - a * m.x, 0, 0);
}

// Applies the smoothed model at full size on edge pixels only, then crisps the band (smoothstep 0.15..0.85).
[numthreads(256, 1, 1)]
void GuideApply(uint3 id : SV_DispatchThreadID)
{
    uint k = Index(id);
    if (k >= (uint)(W * H)) return;
    float o = AlphaOut[k];
    if (o <= 0.001f || o >= 0.999f) return;
    int x = k % W, y = k / W;
    float gy = clamp((y + 0.5f) * ((float)GH / H) - 0.5f, 0, GH - 1);
    int y0 = (int)gy, y1 = min(y0 + 1, GH - 1); float dy = gy - y0;
    float gx = clamp((x + 0.5f) * ((float)GW / W) - 0.5f, 0, GW - 1);
    int x0 = (int)gx, x1 = min(x0 + 1, GW - 1); float dx = gx - x0;
    float2 t = Acc[y0 * GW + x0].xy + (Acc[y0 * GW + x1].xy - Acc[y0 * GW + x0].xy) * dx;
    float2 u = Acc[y1 * GW + x0].xy + (Acc[y1 * GW + x1].xy - Acc[y1 * GW + x0].xy) * dx;
    float2 ab = t + (u - t) * dy;
    float v = clamp((ab.x * Lum(Src[k]) + ab.y - 0.15f) / 0.7f, 0, 1);
    AlphaOut[k] = v * v * (3 - 2 * v);
}

bool IsSkin(float r, float g, float b)
{
    float cb = 128 - 0.168736f * r - 0.331264f * g + 0.5f * b;
    float cr = 128 + 0.5f * r - 0.418688f * g - 0.081312f * b;
    return cb >= 77 && cb <= 135 && cr >= 133 && cr <= 180;
}

[numthreads(256, 1, 1)]
void Retouch(uint3 id : SV_DispatchThreadID)
{
    uint p = Index(id);
    if (p >= (uint)(W * H)) return;
    int x = p % W, y = p / W;
    const int r = 3;
    uint s = Src[p];
    Dst[p] = s;
    if (x < r || x >= W - r || y < r || y >= H - r) return;
    float a = Alpha[p];
    if (a < 0.3f) return;
    float4 c = Unpack(s);
    if (!IsSkin(c.z, c.y, c.x)) return;
    float3 sum = 0; float sw = 0;
    for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            float3 n = Unpack(Src[(y + dy) * W + x + dx]).xyz;
            float diff = abs(n.x - c.x) + abs(n.y - c.y) + abs(n.z - c.z);
            if (diff > 40) continue;
            float wt = 1 - diff / 48;
            sum += n * wt; sw += wt;
        }
    float k = F0.y * a * 0.85f;
    Dst[p] = Pack(uint4(Sat3(c.xyz + (sum / sw - c.xyz) * k), (uint)c.w));
}

[numthreads(256, 1, 1)]
void BlurDown(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    int bw = I1.x, bh = I1.y;
    if (q >= (uint)(bw * bh)) return;
    int x = q % bw, y = q / bw;
    float4 acc = 0;
    for (int yy = 0; yy < 4; yy++)
    {
        int sy = min(H - 1, y * 4 + yy);
        for (int xx = 0; xx < 4; xx++)
        {
            int sx = min(W - 1, x * 4 + xx);
            float wt = 1 - Alpha[sy * W + sx] + 0.02f;
            acc += float4(Unpack(Src[sy * W + sx]).xyz * wt, wt);
        }
    }
    AccOut[q] = acc;
}

[numthreads(256, 1, 1)]
void BoxH(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    int bw = I1.x, bh = I1.y, r = I1.z;
    if (q >= (uint)(bw * bh)) return;
    int x = q % bw, row = q - x;
    float4 sum = 0;
    for (int k = -r; k <= r; k++) sum += Acc[row + clamp(x + k, 0, bw - 1)];
    AccOut[q] = sum / (2 * r + 1);
}

[numthreads(256, 1, 1)]
void BoxV(uint3 id : SV_DispatchThreadID)
{
    uint q = Index(id);
    int bw = I1.x, bh = I1.y, r = I1.z;
    if (q >= (uint)(bw * bh)) return;
    int x = q % bw, y = q / bw;
    float4 sum = 0;
    for (int k = -r; k <= r; k++) sum += Acc[clamp(y + k, 0, bh - 1) * bw + x];
    AccOut[q] = sum / (2 * r + 1);
}

float3 Blurred(int q) { float4 a = Acc[q]; return a.xyz / max(1e-4f, a.w); }

float Curve(float v, float c) { return (v - 0.5f) * c + 0.5f; }

// Same looks as ImageFilters.ApplyPreset; works on (r, g, b).
float3 Preset(int f, float3 c)
{
    float r = c.x, g = c.y, b = c.z;
    float gray = 0.299f * r + 0.587f * g + 0.114f * b;
    switch (f)
    {
        case 1: r = g = b = Curve(gray, 1.15f); break;
        case 2: r = r * 1.08f + 0.02f; g *= 1.02f; b *= 0.88f; break;
        case 3: r *= 0.9f; b = b * 1.1f + 0.02f; break;
        case 4: { float l = Curve(gray, 1.4f); r = r * 0.45f + l * 0.55f; g = g * 0.45f + l * 0.55f; b = b * 0.45f + l * 0.55f; break; }
        case 5: r = Curve(r, 1.15f) + (gray - 0.5f) * 0.08f; b = Curve(b, 1.15f) - (gray - 0.5f) * 0.12f; g = Curve(g, 1.1f); break;
        case 6:
            r = gray + (r - gray) * 1.35f; g = gray + (g - gray) * 1.35f; b = gray + (b - gray) * 1.35f;
            r = Curve(r, 1.1f); g = Curve(g, 1.1f); b = Curve(b, 1.1f); break;
        case 7:
            r = r * 0.82f + 0.1f; g = g * 0.82f + 0.1f; b = b * 0.82f + 0.12f;
            r = gray + (r - gray) * 0.8f; g = gray + (g - gray) * 0.8f; b = gray + (b - gray) * 0.8f; break;
        case 8: r = Curve(r * 1.1f + 0.03f, 1.05f); g = Curve(g * 1.03f + 0.015f, 1.05f); b = Curve(b * 0.85f, 1.05f); break;
        case 9:
        {
            float t = gray - 0.5f;
            r = r + t * 0.10f + 0.02f; g = g + 0.01f; b = b - t * 0.16f;
            r = Curve(r, 1.08f); g = Curve(g, 1.05f); b = Curve(b, 1.08f); break;
        }
        case 10:
            r = 0.06f + r * 0.9f; g = 0.05f + g * 0.88f; b = 0.08f + b * 0.82f;
            r = gray + (r - gray) * 0.9f; g = gray + (g - gray) * 0.9f; b = gray + (b - gray) * 0.9f; break;
        case 11: r = Curve(gray + (r - gray) * 1.2f, 1.22f); g = Curve(gray + (g - gray) * 1.2f, 1.22f); b = Curve(gray + (b - gray) * 1.2f, 1.22f); break;
        case 12: { float n = Curve(gray, 1.45f) - 0.03f; r = g = b = n; break; }
        case 13:
            r = 0.15f + r * 0.8f; g = 0.15f + g * 0.8f; b = 0.17f + b * 0.8f;
            r = gray + (r - gray) * 0.7f; g = gray + (g - gray) * 0.7f; b = gray + (b - gray) * 0.7f; break;
    }
    return float3(r, g, b);
}

float3 LutSample(float3 c)
{
    int size = I2.z, n = size - 1;
    float3 f = clamp((c - F7.xyz) / (F8.xyz - F7.xyz), 0, 1) * n;
    int3 i0 = (int3)f, i1 = min(i0 + 1, n);
    float3 d = f - i0;
    float3 o = 0;
    [unroll] for (int k = 0; k < 8; k++)
    {
        int3 s = int3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
        int3 i = s ? i1 : i0;
        float3 wv = s ? d : 1 - d;
        int idx = ((i.z * size + i.y) * size + i.x) * 3;
        o += float3(Lut[idx], Lut[idx + 1], Lut[idx + 2]) * (wv.x * wv.y * wv.z);
    }
    return o;
}

// ImageFilters.ApplyPixel on one pixel (b, g, r bytes) → bytes.
uint3 Colour(uint3 bgr)
{
    int flags = I2.x;
    float b = Tone[bgr.x], g = Tone[bgr.y], r = Tone[bgr.z];
    if (flags & F_TEMP) { float t = F1.x * 0.1f; r += t; b -= t; }
    if (flags & F_TINT) { float t = F1.y * 0.1f; g -= t; r += t * 0.5f; b += t * 0.5f; }
    if (flags & F_HUE)
    {
        float nr = r * F2.x + g * F2.y + b * F2.z, ng = r * F2.w + g * F3.x + b * F3.y, nb = r * F3.z + g * F3.w + b * F4.x;
        r = nr; g = ng; b = nb;
    }
    float gray = 0.299f * r + 0.587f * g + 0.114f * b;
    float sat = F1.z;
    if (flags & F_VIB)
    {
        float mx = max(r, max(g, b)), mn = min(r, min(g, b));
        sat *= 1 + F1.w * (1 - (mx - mn));
    }
    if (sat != 1) { r = gray + (r - gray) * sat; g = gray + (g - gray) * sat; b = gray + (b - gray) * sat; }
    if (I2.y != 0) { float3 o = Preset(I2.y, float3(r, g, b)); r = o.x; g = o.y; b = o.z; }
    if (flags & F_LUT)
    {
        float3 l = LutSample(clamp(float3(r, g, b), 0, 1));
        float s = F0.z;
        r += (l.x - r) * s; g += (l.y - g) * s; b += (l.z - b) * s;
    }
    return uint3(ToByte(b), ToByte(g), ToByte(r));
}

// Background mode, spotlight, "behind me" layers and colour, in the CPU path's order.
[numthreads(256, 1, 1)]
void Effects(uint3 id : SV_DispatchThreadID)
{
    uint p = Index(id);
    if (p >= (uint)(W * H)) return;
    int x = p % W, y = p / W, mode = I1.w, flags = I2.x;
    float4 c = Unpack(Src[p]);
    float a = mode != 0 || (flags & (F_SPOT | F_BEHIND)) ? Alpha[p] : 1;
    float ia = 1 - a;

    if (mode == 1 && a < 0.999f)
    {
        int bw = I1.x, bh = I1.y;
        float fy = clamp((y + 0.5f) * ((float)bh / H) - 0.5f, 0, bh - 1);
        int y0 = (int)fy, y1 = min(y0 + 1, bh - 1); float dy = fy - y0;
        float fx = clamp((x + 0.5f) * ((float)bw / W) - 0.5f, 0, bw - 1);
        int x0 = (int)fx, x1 = min(x0 + 1, bw - 1); float dx = fx - x0;
        float3 p00 = Blurred(y0 * bw + x0), p01 = Blurred(y0 * bw + x1), p10 = Blurred(y1 * bw + x0), p11 = Blurred(y1 * bw + x1);
        float3 top = p00 + (p01 - p00) * dx, bot = p10 + (p11 - p10) * dx;
        float3 bl = top + (bot - top) * dy;
        float3 fg = c.xyz + (bl - c.xyz) * LIGHT_WRAP * ia; // room light spills onto the edge
        c = float4(Sat3(fg * a + bl * ia), 255);
    }
    else if (mode == 2 && a < 0.999f)
    {
        float3 bg = Unpack(Aux[p]).xyz;
        float3 fg = c.xyz + (bg - c.xyz) * (LIGHT_WRAP * ia);
        c = float4((uint3)(fg * a + bg * ia), 255);
    }
    else if (mode == 3)
    {
        c = float4((uint3)(c.xyz * a), (uint)(a * 255));
    }

    if (flags & F_SPOT)
    {
        float dim = 1 - 0.55f * F0.x, lift = 1 + 0.12f * F0.x;
        c.xyz = Sat3(c.xyz * (dim + (lift - dim) * a));
    }

    if (flags & F_BEHIND)
    {
        float4 o = Unpack(Behind[p]);
        if (o.w > 0)
        {
            float show = ia * o.w / 255;
            if (show > 0) c.xyz = Sat3(c.xyz * (1 - show) + o.xyz * 255 / o.w * show);
        }
    }

    if (flags & F_COLOUR) c.xyz = Colour((uint3)c.xyz);
    Dst[p] = Pack((uint4)c);
}

[numthreads(256, 1, 1)]
void Sharpen(uint3 id : SV_DispatchThreadID)
{
    uint p = Index(id);
    if (p >= (uint)(W * H)) return;
    int x = p % W, y = p / W;
    uint s = Src[p];
    if (x < 1 || x >= W - 1 || y < 1 || y >= H - 1) { Dst[p] = s; return; }
    int3 c = (int3)Unpack(s).xyz;
    int3 blur = ((int3)Unpack(Src[p - W]).xyz + (int3)Unpack(Src[p + W]).xyz + (int3)Unpack(Src[p - 1]).xyz + (int3)Unpack(Src[p + 1]).xyz) >> 2;
    float3 v = (c + (c - blur) * F0.w) / 255.0f;
    Dst[p] = Pack(uint4(ToByte(v.x), ToByte(v.y), ToByte(v.z), s >> 24));
}

// SceneCompositor.BlendOver: premultiplied source-over in integer maths.
uint4 Over(uint4 d, uint4 s)
{
    if (s.w == 0) return d;
    if (s.w == 255) return uint4(s.xyz, 255);
    uint inv = 255 - s.w;
    return min(255, s + d * inv / 255);
}

float EllipseCoverage(float x, float y, int w, int h)
{
    float nx = (x - w / 2.0f) / (w / 2.0f), ny = (y - h / 2.0f) / (h / 2.0f);
    float d = sqrt(nx * nx + ny * ny);
    float edge = 1.5f / min(w, h) * 2;
    return clamp((1 - d) / edge + 0.5f, 0, 1);
}

float RoundRectCoverage(float x, float y, int w, int h, float r)
{
    r = min(r, min(w, h) / 2.0f);
    if (r <= 0.5f) return 1;
    float cx = clamp(x, r, w - r), cy = clamp(y, r, h - r);
    float d = sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
    return clamp(r - d + 0.5f, 0, 1);
}

[numthreads(256, 1, 1)]
void Scene(uint3 id : SV_DispatchThreadID)
{
    uint p = Index(id);
    if (p >= (uint)(W * H)) return;
    int x = p % W, y = p / W, sf = I4.x;
    float t = H > 1 ? (float)y / (H - 1) : 0;
    uint4 bg = uint4((uint3)(F5.xyz + (F6.xyz - F5.xyz) * t), 255);
    uint4 o;
    if (sf & S_FILLS)
    {
        o = (uint4)Unpack(Src[p]);
        if (sf & S_UNDERLAY) o = Over(bg, o);
    }
    else
    {
        o = bg;
        int lx = x - I3.x, ly = y - I3.y, rw = I3.z, rh = I3.w;
        if ((sf & S_CAMERA) && lx >= 0 && ly >= 0 && lx < rw && ly < rh)
        {
            float cover = (sf & S_ELLIPSE) ? EllipseCoverage(lx + 0.5f, ly + 0.5f, rw, rh) : RoundRectCoverage(lx + 0.5f, ly + 0.5f, rw, rh, F4.z);
            if (cover > 0)
            {
                // OutputFit.Cover of the camera into the layer rectangle, sampled on the fly.
                float scale = F9.z;
                float fy = clamp(F9.y + (ly + 0.5f) / scale - 0.5f, 0, H - 1);
                int y0 = (int)fy, y1 = min(y0 + 1, H - 1); float dy = fy - y0;
                float fx = clamp(F9.x + (lx + 0.5f) / scale - 0.5f, 0, W - 1);
                int x0 = (int)fx, x1 = min(x0 + 1, W - 1); float dx = fx - x0;
                float4 a = Unpack(Src[y0 * W + x0]), b = Unpack(Src[y0 * W + x1]), c = Unpack(Src[y1 * W + x0]), d = Unpack(Src[y1 * W + x1]);
                float4 top = a + (b - a) * dx, bot = c + (d - c) * dx;
                float4 fit = round(top + (bot - top) * dy);
                float k = cover * F4.y;
                float al = k * fit.w / 255;
                o = uint4(Sat3(fit.xyz * k + o.xyz * (1 - al)), 255);
            }
        }
    }
    if (sf & S_OVERLAY) o = Over(o, (uint4)Unpack(Aux[p]));
    Dst[p] = Pack(o);
}
