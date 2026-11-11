import { MyComposition } from "./Composition";
import { OgComposition } from "./Og";
import { FeatureCompositions } from "./Features";
import { PairingComposition } from "./Pairing";
import { PromoComposition } from "./Promo";
import { PromoFastCompositions } from "./PromoFast";
import { StoryCompositions } from "./Story";
import { IntroCompositions } from "./Intro";

export const RemotionRoot: React.FC = () => {
  return (
    <>
      <MyComposition />
      <OgComposition />
      <FeatureCompositions />
      <PairingComposition />
      <PromoComposition />
      <PromoFastCompositions />
      <StoryCompositions />
      <IntroCompositions />
    </>
  );
};
