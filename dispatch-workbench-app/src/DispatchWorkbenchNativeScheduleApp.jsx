import NativeScheduleDemoPage from "./NativeScheduleDemoPage";
import { useNativeScheduleI18n } from "./native-schedule-i18n";

export default function DispatchWorkbenchNativeScheduleApp({ registerHostActions }) {
  const { locale, script } = useNativeScheduleI18n();

  return (
    <div
      className="dw-native-schedule-root"
      data-native-schedule-locale={locale}
      data-native-schedule-script={script}
      lang={locale}
    >
      <NativeScheduleDemoPage registerHostActions={registerHostActions} />
    </div>
  );
}
