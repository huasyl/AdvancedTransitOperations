const VARIABLE_LIBRARY = [
  { id: "current_station", nameKey: "broadcast.variable.current", descKey: "" },
  { id: "next_station", nameKey: "broadcast.variable.next", descKey: "" },
  {
    id: "terminal_station",
    nameKey: "broadcast.variable.terminal",
    descKey: "",
  },
  {
    id: "turnback_station",
    nameKey: "broadcast.variable.turnback",
    descKey: "",
  },
];

const DELAY_LIBRARY = [
  {
    id: "delay_03",
    nameKey: "broadcast.delay.03",
    descKey: "broadcast.delay.label",
    delaySeconds: 0.3,
  },
  {
    id: "delay_05",
    nameKey: "broadcast.delay.05",
    descKey: "broadcast.delay.label",
    delaySeconds: 0.5,
  },
  {
    id: "delay_08",
    nameKey: "broadcast.delay.08",
    descKey: "broadcast.delay.label",
    delaySeconds: 0.8,
  },
  {
    id: "delay_10",
    nameKey: "broadcast.delay.10",
    descKey: "broadcast.delay.label",
    delaySeconds: 1,
  },
  {
    id: "delay_20",
    nameKey: "broadcast.delay.20",
    descKey: "broadcast.delay.label",
    delaySeconds: 2,
  },
];

const BROADCAST_LANGUAGE_ALIASES = {
  en: ["eng", "en", "english"],
  zh: ["zh", "cn", "chi", "chs", "cht", "chinese", "mandarin"],
  ja: ["ja", "jp", "jpn", "japanese"],
  ko: ["ko", "kr", "kor", "korean"],
  yue: ["yue", "cantonese"],
  fr: ["fr", "fre", "fra", "french"],
  de: ["de", "ger", "deu", "german"],
  es: ["es", "spa", "spanish"],
  ru: ["ru", "rus", "russian"],
  pt: ["pt", "por", "portuguese"],
  th: ["th", "tha", "thai"],
  ar: ["ar", "ara", "arabic"],
};

const BROADCAST_LANGUAGE_LABEL_KEYS = {
  en: "broadcast.language.short.en",
  zh: "broadcast.language.short.zh",
  ja: "broadcast.language.short.ja",
  ko: "broadcast.language.short.ko",
  yue: "broadcast.language.short.yue",
  fr: "broadcast.language.short.fr",
  de: "broadcast.language.short.de",
  es: "broadcast.language.short.es",
  ru: "broadcast.language.short.ru",
  pt: "broadcast.language.short.pt",
  th: "broadcast.language.short.th",
  ar: "broadcast.language.short.ar",
};

const BROADCAST_LANGUAGE_DISPLAY_ALIASES = {
  en: ["英", "eng"],
  zh: ["中", "chi"],
  ja: ["日", "jpn"],
  ko: ["韩", "韓", "kor"],
  yue: ["粤"],
  fr: ["法", "仏", "fre"],
  de: ["德", "独", "ger"],
  es: ["西", "spa"],
  ru: ["俄", "露", "rus"],
  pt: ["葡", "por"],
  th: ["泰", "tha"],
  ar: ["阿", "ara"],
};

const TRIGGER_OPTIONS = [
  { id: "approach_station", labelKey: "broadcast.trigger.approachStation" },
  { id: "stop_and_open", labelKey: "broadcast.trigger.stopAndOpen" },
  { id: "leave_station", labelKey: "broadcast.trigger.leaveStation" },
  { id: "mid_route", labelKey: "broadcast.trigger.midRoute" },
  { id: "bypass_waiting", labelKey: "broadcast.trigger.bypassWaiting" },
];

const PLATFORM_TRIGGER_OPTIONS = [
  { id: "approach_station", labelKey: "broadcast.trigger.approachStation" },
  { id: "platform_idle_clear", labelKey: "broadcast.platform.idleClear" },
];

const RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS = ["bypass_waiting"];
const RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS = ["platform_idle_clear"];

function resolvePlatformUiTriggerId(triggerId) {
  if (
    triggerId === "approach_station" ||
    triggerId === "platform_approach_station"
  ) {
    return "approach_station";
  }
  return "platform_idle_clear";
}

function resolvePlatformRuntimeTriggerId(triggerId) {
  return resolvePlatformUiTriggerId(triggerId) === "approach_station"
    ? "platform_approach_station"
    : "platform_idle_clear";
}

const LINE_OPTIONS = [
  { id: "line_1_main", labelKey: "broadcast.line.main" },
  { id: "airport_express", labelKey: "broadcast.line.airport" },
  { id: "loop_test", labelKey: "broadcast.line.loop" },
];

const EXTERNAL_ASSET_FILE_SYSTEM = {
  "C:\\Mods\\Audio\\": {
    folders: ["BGM", "SFX", "Voice_Packs"],
    files: [{ id: "root_f1", name: "Global_Config_Ping.wav" }],
  },
  "C:\\Mods\\Audio\\BGM\\": {
    folders: [],
    files: [
      { id: "bgm_1", name: "Ambient_City.wav" },
      { id: "bgm_2", name: "Menu_Theme.ogg" },
    ],
  },
  "C:\\Mods\\Audio\\SFX\\": {
    folders: ["Vehicles", "UI"],
    files: [],
  },
  "C:\\Mods\\Audio\\SFX\\Vehicles\\": {
    folders: [],
    files: [
      { id: "veh_1", name: "Train_Whistle.ogg" },
      { id: "veh_2", name: "Bus_Brake.wav" },
    ],
  },
  "C:\\Mods\\Audio\\SFX\\UI\\": {
    folders: [],
    files: [{ id: "ui_1", name: "Notification_Ping.mp3" }],
  },
  "C:\\Mods\\Audio\\Voice_Packs\\": {
    folders: [],
    files: [
      { id: "vp_1", name: "Station_Jingmai.wav" },
      { id: "vp_2", name: "Next_Stop_Is.wav" },
    ],
  },
};

const DEFAULT_EXTERNAL_ASSET_PATH = "C:\\Mods\\Audio\\";
const TAB_TRANSITION_MS = 300;
const INLINE_PANEL_TRANSITION_MS = 650;
const INLINE_PANEL_EASING = "cubic-bezier(0.19, 1, 0.22, 1)";
const PAGE_ENTER_ANIMATION_MS = 850;
const IMPORT_OVERLAY_TRANSITION_MS = 350;

export {
  VARIABLE_LIBRARY,
  DELAY_LIBRARY,
  BROADCAST_LANGUAGE_ALIASES,
  BROADCAST_LANGUAGE_LABEL_KEYS,
  BROADCAST_LANGUAGE_DISPLAY_ALIASES,
  TRIGGER_OPTIONS,
  PLATFORM_TRIGGER_OPTIONS,
  RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS,
  RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS,
  LINE_OPTIONS,
  EXTERNAL_ASSET_FILE_SYSTEM,
  DEFAULT_EXTERNAL_ASSET_PATH,
  TAB_TRANSITION_MS,
  INLINE_PANEL_TRANSITION_MS,
  INLINE_PANEL_EASING,
  PAGE_ENTER_ANIMATION_MS,
  IMPORT_OVERLAY_TRANSITION_MS,
  resolvePlatformUiTriggerId,
  resolvePlatformRuntimeTriggerId,
};
