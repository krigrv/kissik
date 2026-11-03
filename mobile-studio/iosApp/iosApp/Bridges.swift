import Foundation
import AVFoundation
import VideoToolbox
import CoreMedia
import HaishinKit
import Shared

/// Installs the Swift implementations of the Kotlin bridges (shared/src/iosMain/.../IosBridges.kt).
enum KissikBridges {
    static func install() {
        Bridges.shared.rtmp = RtmpPublisher()
        Bridges.shared.h264 = H264Encoder()
    }
}

// MARK: - Pixel buffers from KISSIK frames

/// Wraps BGRA bytes (stride = width × 4) in a CVPixelBuffer.
func makePixelBuffer(_ bgra: Data, width: Int, height: Int) -> CVPixelBuffer? {
    var pb: CVPixelBuffer?
    let attrs = [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary
    guard CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_32BGRA, attrs, &pb) == kCVReturnSuccess, let buf = pb else { return nil }
    CVPixelBufferLockBaseAddress(buf, [])
    let dst = CVPixelBufferGetBaseAddress(buf)!
    let stride = CVPixelBufferGetBytesPerRow(buf)
    bgra.withUnsafeBytes { src in
        for y in 0..<height { memcpy(dst + y * stride, src.baseAddress! + y * width * 4, width * 4) }
    }
    CVPixelBufferUnlockBaseAddress(buf, [])
    return buf
}

func makeSampleBuffer(_ pb: CVPixelBuffer, timeUs: Int64) -> CMSampleBuffer? {
    var fmt: CMVideoFormatDescription?
    CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: pb, formatDescriptionOut: &fmt)
    guard let format = fmt else { return nil }
    var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: CMTime(value: timeUs, timescale: 1_000_000), decodeTimeStamp: .invalid)
    var sb: CMSampleBuffer?
    CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: pb, formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sb)
    return sb
}

// MARK: - RTMP (HaishinKit, BSD-3-Clause)

/// One RTMP connection + stream per destination, all fed the same frames.
final class RtmpPublisher: NSObject, RtmpBridge {
    private var outputs: [(id: String, connection: RTMPConnection, stream: RTMPStream)] = []
    private var report: ((String, String) -> Void)?

    func start(destinations: [[String]], width: Int32, height: Int32, fps: Int32, bitrateKbps: Int32, hevc: Bool, state: @escaping (String, String) -> Void) {
        report = state
        for d in destinations where d.count == 2 {
            let id = d[0], full = d[1]
            // Publish URL is server + "/" + key: split at the last slash.
            guard let cut = full.range(of: "/", options: .backwards) else { state(id, "Failed"); continue }
            let server = String(full[..<cut.lowerBound]), key = String(full[cut.upperBound...])
            let connection = RTMPConnection()
            let stream = RTMPStream(connection: connection)
            outputs.append((id, connection, stream))
            state(id, "Connecting")
            Task {
                do {
                    var video = VideoCodecSettings(videoSize: CGSize(width: Int(width), height: Int(height)), bitRate: Int(bitrateKbps) * 1000)
                    if hevc { video.format = .hevc }
                    try await stream.setVideoSettings(video)
                    try await stream.setAudioSettings(AudioCodecSettings(bitRate: 128_000))
                    _ = try await connection.connect(server)
                    _ = try await stream.publish(key)
                    state(id, "Live")
                } catch {
                    state(id, "Failed")
                }
            }
        }
    }

    func video(bgra: Data, width: Int32, height: Int32, timeUs: Int64) {
        guard !outputs.isEmpty, let pb = makePixelBuffer(bgra, width: Int(width), height: Int(height)),
              let sb = makeSampleBuffer(pb, timeUs: timeUs) else { return }
        for o in outputs { Task { await o.stream.append(sb) } }
    }

    func audio(samples: Data, sampleRate: Int32, timeUs: Int64) {
        guard !outputs.isEmpty,
              let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(sampleRate), channels: 1, interleaved: false),
              let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count / 4)) else { return }
        buf.frameLength = buf.frameCapacity
        samples.withUnsafeBytes { memcpy(buf.floatChannelData![0], $0.baseAddress!, samples.count) }
        let when = AVAudioTime(hostTime: mach_absolute_time())
        for o in outputs { Task { await o.stream.append(buf, when: when) } }
    }

    func stop() {
        for o in outputs { Task { _ = try? await o.stream.close(); try? await o.connection.close() } }
        outputs.removeAll()
    }
}

// MARK: - H.264 for the PC webcam link (VideoToolbox)

/// Hardware H.264 encoder producing Annex-B access units; keyframes carry SPS + PPS in front.
final class H264Encoder: NSObject, H264Bridge {
    private var session: VTCompressionSession?
    private var output: ((Data, KotlinBoolean, KotlinLong) -> Void)?
    private var forceKey = false

    func start(width: Int32, height: Int32, fps: Int32, bitrateKbps: Int32, output: @escaping (Data, KotlinBoolean, KotlinLong) -> Void) {
        self.output = output
        VTCompressionSessionCreate(allocator: nil, width: width, height: height, codecType: kCMVideoCodecType_H264,
                                   encoderSpecification: nil, imageBufferAttributes: nil, compressedDataAllocator: nil,
                                   outputCallback: nil, refcon: nil, compressionSessionOut: &session)
        guard let s = session else { return }
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: Int(bitrateKbps) * 1000))
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: Int(fps)))
        VTCompressionSessionPrepareToEncodeFrames(s)
    }

    func encode(bgra: Data, width: Int32, height: Int32, timeUs: Int64) {
        guard let s = session, let pb = makePixelBuffer(bgra, width: Int(width), height: Int(height)) else { return }
        let props: CFDictionary? = forceKey ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        forceKey = false
        VTCompressionSessionEncodeFrame(s, imageBuffer: pb, presentationTimeStamp: CMTime(value: timeUs, timescale: 1_000_000),
                                        duration: .invalid, frameProperties: props, infoFlagsOut: nil) { [weak self] status, _, sample in
            guard status == noErr, let sample = sample, let self = self else { return }
            let (data, key) = Self.annexB(sample)
            self.output?(data, KotlinBoolean(bool: key), KotlinLong(value: timeUs))
        }
    }

    func requestKeyFrame() { forceKey = true }

    func stop() {
        if let s = session { VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid); VTCompressionSessionInvalidate(s) }
        session = nil; output = nil
    }

    /// AVCC (length-prefixed) → Annex-B (start codes), with SPS/PPS in front of keyframes.
    static func annexB(_ sample: CMSampleBuffer) -> (Data, Bool) {
        let start: [UInt8] = [0, 0, 0, 1]
        var out = Data()
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]]
        let key = !(attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
        if key, let fmt = CMSampleBufferGetFormatDescription(sample) {
            for i in 0..<2 {
                var ptr: UnsafePointer<UInt8>?; var size = 0
                if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: i, parameterSetPointerOut: &ptr,
                                                                      parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr, let p = ptr {
                    out.append(contentsOf: start); out.append(p, count: size)
                }
            }
        }
        guard let block = CMSampleBufferGetDataBuffer(sample) else { return (out, key) }
        var length = 0; var pointer: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &pointer)
        guard let base = pointer else { return (out, key) }
        var offset = 0
        while offset + 4 <= length {
            var nalLen: UInt32 = 0
            memcpy(&nalLen, base + offset, 4)
            let n = Int(CFSwapInt32BigToHost(nalLen))
            out.append(contentsOf: start)
            out.append(Data(bytes: base + offset + 4, count: n))
            offset += 4 + n
        }
        return (out, key)
    }
}
