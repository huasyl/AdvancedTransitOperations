import { nativeScheduleTranslationsEnUs } from "./workbench-translations.en-US";
import { nativeScheduleTranslationsJaJp } from "./workbench-translations.ja-JP";
import { nativeScheduleTranslationsZhCn } from "./workbench-translations.zh-CN";
import { passengerTranslations } from "./workbench/pages/passenger/passenger-translations";

export const nativeScheduleTranslations = {
  "en-US": { ...nativeScheduleTranslationsEnUs, ...passengerTranslations["en-US"] },
  "ja-JP": { ...nativeScheduleTranslationsJaJp, ...passengerTranslations["ja-JP"] },
  "zh-CN": { ...nativeScheduleTranslationsZhCn, ...passengerTranslations["zh-CN"] }
};
