import { MyComposition } from "./Composition";
import { OgComposition } from "./Og";
import { FeatureCompositions } from "./Features";
import { PairingComposition } from "./Pairing";

export const RemotionRoot: React.FC = () => {
  return (
    <>
      <MyComposition />
      <OgComposition />
      <FeatureCompositions />
      <PairingComposition />
    </>
  );
};
