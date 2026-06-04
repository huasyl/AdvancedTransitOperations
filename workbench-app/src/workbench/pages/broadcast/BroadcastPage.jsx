import useBroadcastController from "./useBroadcastController";
import BroadcastLayout from "./components/BroadcastLayout";

export default function BroadcastPage({ pageEnterSequence = 0 }) {
  const controller = useBroadcastController({ pageEnterSequence });
  return <BroadcastLayout controller={controller} />;
}
