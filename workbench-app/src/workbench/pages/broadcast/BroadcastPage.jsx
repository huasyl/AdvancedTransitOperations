import useBroadcastController from "./useBroadcastController";
import BroadcastLayout from "./components/BroadcastLayout";

export default function BroadcastPage({ pageEnterSequence = 0, activeTransportMode = "train" }) {
  const controller = useBroadcastController({ pageEnterSequence, activeTransportMode });
  return <BroadcastLayout controller={controller} />;
}
