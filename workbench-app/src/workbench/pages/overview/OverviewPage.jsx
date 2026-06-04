export default function OverviewPage({ labels }) {
  return (
    <div className="dw-native-workbench-overview">
      <div className="dw-native-workbench-overview-card">
        <div className="dw-native-workbench-overview-kicker">{labels.overviewKicker}</div>
        <div className="dw-native-workbench-overview-title">{labels.overviewTitle}</div>
        <div className="dw-native-workbench-overview-copy">
          {labels.overviewCopy}
        </div>
      </div>
    </div>
  );
}
