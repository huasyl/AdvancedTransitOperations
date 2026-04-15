import NativeScheduleDemoPage from "./NativeScheduleDemoPage";

export default function DispatchWorkbenchNativeScheduleApp({ registerHostActions }) {
  if (typeof registerHostActions === "function") {
    registerHostActions(null);
  }

  return (
    <div className="dw-native-schedule-root">
      <NativeScheduleDemoPage />
    </div>
  );
}
