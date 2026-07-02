using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ATL;
using Colossal.Core;
using Game;
using Game.Audio;
using Game.UI.InGame;
using Game.UI.Menu;
using Game.Routes;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
using RapidTransitMod;
using RapidTransitMod.Broadcasting;
using IoPath = System.IO.Path;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Preview : ModuleBase
    {
        private AudioSource m_Source;
        private AudioClip m_Clip;
        private string m_AssetName = string.Empty;
        private int m_Token;
        private AudioSource m_RuleSource;
        private AudioClip m_RuleClip;
        private string m_RuleId = string.Empty;
        private int m_RuleToken;
        private static readonly FieldInfo s_AudioManagerUiGroupField =
            typeof(AudioManager).GetField("m_UIGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        internal Preview(Context context) : base(context) { }

                public string PlayBroadcastAssetPreviewJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "playBroadcastAssetPreview");
                    string assetName = Workbenches.ModeRequest.ReadAssetName(requestJson);
                    BroadcastWorkbenchAssetPreviewResult result = new BroadcastWorkbenchAssetPreviewResult
                    {
                        success = false,
                        state = "error",
                        error = string.Empty,
                        assetName = assetName ?? string.Empty
                    };

                    try
                    {
                        using (UseScope(scope))
                        {
                        string requestedAssetName = assetName ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(requestedAssetName))
                        {
                            result.error = "Asset name is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        string modeToken = scope.Token;
                        MainThreadDispatcher.RunOnMainThread(() =>
                            StopRule(m_RuleId, notify: true, modeToken: modeToken));

                        MainThreadDispatcher.RunOnMainThread(async () =>
                        {
                            using (UseScope(scope))
                            {
                            try
                            {
                                await PlayAsset(requestedAssetName);
                            }
                            catch (Exception ex)
                            {
                                NotifyAsset(requestedAssetName, "error", ex.Message ?? string.Empty);
                                LogException("PlayAsset", ex);
                            }
                            }
                        });

                        result.success = true;
                        result.state = "pending";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("PlayBroadcastAssetPreviewJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string PlayBroadcastRulePreviewJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "playBroadcastRulePreview");
                    BroadcastWorkbenchRulePreviewResult result = new BroadcastWorkbenchRulePreviewResult
                    {
                        success = false,
                        state = "error",
                        error = string.Empty,
                        ruleId = string.Empty
                    };

                    try
                    {
                        using (UseScope(scope))
                        {
                        LoadWorkbench();
                        BroadcastWorkbenchRulePreviewRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchRulePreviewRequest>(requestJson);
                        string lineId = scope.NormalizeLineId(request?.lineId);
                        string ruleId = request?.ruleId ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(ruleId))
                        {
                            result.error = "Line or rule is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        result.success = true;
                        result.state = "pending";
                        result.ruleId = ruleId;
                        BroadcastWorkbenchRuleDto previewRule = request?.rule;
                        string modeToken = scope.Token;
                        MainThreadDispatcher.RunOnMainThread(async () =>
                        {
                            using (UseScope(scope))
                            {
                            try
                            {
                                await PlayRule(lineId, ruleId, previewRule, modeToken);
                            }
                            catch (Exception ex)
                            {
                                NotifyRule(modeToken, ruleId, "error", ex.Message ?? string.Empty);
                                LogException("PlayRule", ex);
                            }
                            }
                        });
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("PlayBroadcastRulePreviewJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string StopBroadcastAssetPreviewJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "stopBroadcastAssetPreview");
                    string assetName = Workbenches.ModeRequest.ReadAssetName(requestJson);
                    BroadcastWorkbenchAssetPreviewResult result = new BroadcastWorkbenchAssetPreviewResult
                    {
                        success = true,
                        state = "stopped",
                        error = string.Empty,
                        assetName = assetName ?? string.Empty
                    };

                    try
                    {
                        using (UseScope(scope))
                        {
                        string requestedAssetName = assetName ?? string.Empty;
                        string modeToken = scope.Token;
                        MainThreadDispatcher.RunOnMainThread(() => StopAsset(requestedAssetName, notify: true, modeToken: modeToken));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.success = false;
                        result.state = "error";
                        result.error = ex.Message ?? string.Empty;
                        LogException("StopBroadcastAssetPreviewJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string StopBroadcastRulePreviewJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "stopBroadcastRulePreview");
                    string ruleId = Workbenches.ModeRequest.ReadRuleId(requestJson);
                    BroadcastWorkbenchRulePreviewResult result = new BroadcastWorkbenchRulePreviewResult
                    {
                        success = true,
                        state = "stopped",
                        error = string.Empty,
                        ruleId = ruleId ?? string.Empty
                    };

                    try
                    {
                        using (UseScope(scope))
                        {
                        string requestedRuleId = ruleId ?? string.Empty;
                        string modeToken = scope.Token;
                        MainThreadDispatcher.RunOnMainThread(() => StopRule(requestedRuleId, notify: true, modeToken: modeToken));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.success = false;
                        result.state = "error";
                        result.error = ex.Message ?? string.Empty;
                        LogException("StopBroadcastRulePreviewJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string SetBroadcastPreviewVolumeJson(string requestJson)
                {
                    BroadcastWorkbenchVolumeResult result = new BroadcastWorkbenchVolumeResult
                    {
                        success = false,
                        error = string.Empty,
                        volume = Clamp(DraftVol),
                        volumeDirty = DraftVol != AppliedVol,
                        snapshot = null
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "setBroadcastPreviewVolume");
                        using (UseScope(scope))
                        {
                            int nextVolume = Clamp(Workbenches.ModeRequest.ReadVolume(requestJson, DraftVol));
                            bool changed = nextVolume != DraftVol;
                            DraftVol = nextVolume;
                            ApplyVolume();
                            if (changed)
                            {
                                IncrementWorkbenchSnapshotVersion();
                                SaveWorkbench();
                            }

                            result.success = true;
                            result.volume = Clamp(DraftVol);
                            result.volumeDirty = DraftVol != AppliedVol;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("SetBroadcastPreviewVolumeJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal async Task PlayAsset(string assetName)
                {
                    string requestedAssetName = assetName ?? string.Empty;
                    BroadcastWorkbenchAssetDto asset = Catalog.FirstOrDefault(candidate =>
                        string.Equals(candidate?.name, requestedAssetName, StringComparison.OrdinalIgnoreCase));
                    string assetPath = Assets.Path(asset?.path);
                    if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                    {
                        StopAsset(requestedAssetName, notify: false);
                        NotifyAsset(requestedAssetName, "error", "Selected asset file was not found.");
                        return;
                    }

                    AudioType audioType = AudioType(assetPath);
                    if (audioType == UnityEngine.AudioType.UNKNOWN)
                    {
                        StopAsset(requestedAssetName, notify: false);
                        NotifyAsset(requestedAssetName, "error", "Unsupported audio format.");
                        return;
                    }

                    string previousAssetName = m_AssetName;
                    if (!string.IsNullOrEmpty(previousAssetName))
                    {
                        StopAsset(previousAssetName, notify: true);
                    }

                    int playbackToken = unchecked(++m_Token);
                    using UnityWebRequest request = Preview.Request(assetPath, audioType);
                    DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
                    if (downloadHandler != null)
                    {
                        downloadHandler.streamAudio = false;
                    }

                    await WaitUnityWebRequest(request);
                    if (request.result == UnityWebRequest.Result.ConnectionError
                        || request.result == UnityWebRequest.Result.ProtocolError
                        || request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        NotifyAsset(requestedAssetName, "error", request.error ?? "Audio preview load failed.");
                        return;
                    }

                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null)
                    {
                        NotifyAsset(requestedAssetName, "error", "Audio preview load returned no clip.");
                        return;
                    }

                    if (playbackToken != m_Token)
                    {
                        UnityEngine.Object.Destroy(clip);
                        return;
                    }

                    Source();
                    ReleaseAsset();
                    m_Clip = clip;
                    m_Source.clip = clip;
                    m_Source.loop = false;
                    m_Source.pitch = 1f;
                    m_Source.volume = VolumeScalar(DraftVol);
                    m_Source.timeSamples = 0;
                    AudioManager.AudioSourcePool.Play(m_Source);
                    m_AssetName = requestedAssetName;
                    NotifyAsset(requestedAssetName, "started", string.Empty);
                    MainThreadDispatcher.RegisterUpdater(() => ObserveAsset(playbackToken, requestedAssetName));
                }

                internal void StopAsset(string assetName, bool notify, string modeToken = null)
                {
                    string resolvedAssetName = !string.IsNullOrWhiteSpace(assetName)
                        ? assetName
                        : m_AssetName;
                    unchecked
                    {
                        m_Token++;
                    }

                    if (m_Source != null)
                    {
                        AudioManager audioManager = AudioManager.instance;
                        if (audioManager != null)
                        {
                            audioManager.StopExclusiveUISound(m_Source);
                        }
                        else
                        {
                            AudioManager.AudioSourcePool.Release(m_Source);
                        }

                        m_Source = null;
                    }

                    ReleaseAsset();
                    m_AssetName = string.Empty;

                    if (notify && !string.IsNullOrWhiteSpace(resolvedAssetName))
                    {
                        NotifyAsset(modeToken, resolvedAssetName, "stopped", string.Empty);
                    }
                }

                internal bool ObserveAsset(int playbackToken, string assetName)
                {
                    if (playbackToken != m_Token || m_Source == null)
                    {
                        return true;
                    }

                    if (m_Source.isPlaying)
                    {
                        return false;
                    }

                    bool wasCurrentAsset = string.Equals(m_AssetName, assetName, StringComparison.OrdinalIgnoreCase);
                    StopAsset(assetName, notify: false);
                    if (wasCurrentAsset)
                    {
                        NotifyAsset(assetName, "ended", string.Empty);
                    }

                    return true;
                }

                internal void Source()
                {
                    if (m_Source != null)
                    {
                        return;
                    }

                    m_Source = AudioManager.AudioSourcePool.Get();
                    m_Source.outputAudioMixerGroup = UiGroup();
                    m_Source.dopplerLevel = 0f;
                    m_Source.playOnAwake = false;
                    m_Source.spatialBlend = 0f;
                    m_Source.ignoreListenerPause = true;
                    m_Source.pitch = 1f;
                    m_Source.volume = VolumeScalar(DraftVol);
                }

                internal AudioMixerGroup UiGroup()
                {
                    AudioManager audioManager = AudioManager.instance;
                    if (audioManager == null || s_AudioManagerUiGroupField == null)
                    {
                        return null;
                    }

                    try
                    {
                        return s_AudioManagerUiGroupField.GetValue(audioManager) as AudioMixerGroup;
                    }
                    catch
                    {
                        return null;
                    }
                }

                internal void ReleaseAsset()
                {
                    if (m_Clip == null)
                    {
                        return;
                    }

                    UnityEngine.Object.Destroy(m_Clip);
                    m_Clip = null;
                }

                internal async Task PlayRule(string lineId, string ruleId, BroadcastWorkbenchRuleDto previewRule = null, string modeToken = null)
                {
                    StopAsset(m_AssetName, notify: true);
                    StopRule(ruleId, notify: false);

                    BroadcastWorkbenchRuleDto rule = previewRule != null
                        && string.Equals(previewRule.id, ruleId, StringComparison.Ordinal)
                            ? Rules.Clone(previewRule)
                            : m_Ctx.Rules.DraftRows(lineId).FirstOrDefault(candidate =>
                                candidate != null && string.Equals(candidate.id, ruleId, StringComparison.Ordinal));
                    if (rule?.nodes == null || rule.nodes.Length == 0)
                    {
                        NotifyRule(modeToken, ruleId, "error", "Selected rule has no previewable nodes.");
                        return;
                    }

                    if (!Context(lineId, out TriggerContext context))
                    {
                        NotifyRule(modeToken, ruleId, "error", "Preview context is unavailable.");
                        return;
                    }

                    int playbackToken = unchecked(++m_RuleToken);
                    m_RuleId = ruleId;
                    NotifyRule(modeToken, ruleId, "started", string.Empty);
                    bool playedAnyClip = false;
                    bool skippedMissingAsset = false;

                    for (int nodeIndex = 0; nodeIndex < rule.nodes.Length; nodeIndex++)
                    {
                        if (playbackToken != m_RuleToken)
                        {
                            return;
                        }

                        BroadcastWorkbenchRuleNodeDto node = rule.nodes[nodeIndex];
                        if (node == null)
                        {
                            continue;
                        }

                        if (string.Equals(node.type, "delay", StringComparison.Ordinal))
                        {
                            float delaySeconds = node.delaySeconds > 0f ? node.delaySeconds : 0f;
                            if (delaySeconds > 0)
                            {
                                await Task.Delay(Mathf.Max(1, Mathf.RoundToInt(delaySeconds * 1000f)));
                            }

                            continue;
                        }

                        string assetName = m_Announcements.AssetName(node, context);
                        if (string.IsNullOrWhiteSpace(assetName))
                        {
                            continue;
                        }

                        AudioClip clip = await Load(assetName);
                        if (playbackToken != m_RuleToken)
                        {
                            DestroyClip(clip);
                            return;
                        }

                        if (clip == null)
                        {
                            skippedMissingAsset = true;
                            continue;
                        }

                        playedAnyClip = true;
                        RuleSource();
                        ReleaseRule();
                        m_RuleClip = clip;
                        m_RuleSource.clip = clip;
                        m_RuleSource.loop = false;
                        m_RuleSource.pitch = 1f;
                        m_RuleSource.volume = VolumeScalar(DraftVol);
                        m_RuleSource.timeSamples = 0;
                        AudioManager.AudioSourcePool.Play(m_RuleSource);
                        await Task.Delay(Mathf.Max(1, Mathf.RoundToInt(clip.length * 1000f)));
                    }

                    if (playbackToken != m_RuleToken)
                    {
                        return;
                    }

                    if (skippedMissingAsset)
                    {
                        StopRule(ruleId, notify: false);
                        NotifyRule(modeToken, ruleId, "error", "Selected rule references missing asset files.");
                        return;
                    }

                    StopRule(ruleId, notify: false);
                    NotifyRule(modeToken, ruleId, "ended", string.Empty);
                }

                internal async Task<AudioClip> Load(string assetName)
                {
                    string requestedAssetName = assetName ?? string.Empty;
                    BroadcastWorkbenchAssetDto asset = Catalog.FirstOrDefault(candidate =>
                        string.Equals(candidate?.name, requestedAssetName, StringComparison.OrdinalIgnoreCase));
                    string assetPath = Assets.Path(asset?.path);
                    if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                    {
                        return null;
                    }

                    AudioType audioType = AudioType(assetPath);
                    if (audioType == UnityEngine.AudioType.UNKNOWN)
                    {
                        return null;
                    }

                    return await OnMain(async () =>
                    {
                        using UnityWebRequest request = Preview.Request(assetPath, audioType);
                        DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
                        if (downloadHandler != null)
                        {
                            downloadHandler.streamAudio = false;
                        }

                        await WaitUnityWebRequest(request);
                        if (request.result == UnityWebRequest.Result.ConnectionError
                            || request.result == UnityWebRequest.Result.ProtocolError
                            || request.result == UnityWebRequest.Result.DataProcessingError)
                        {
                            return null;
                        }

                        return DownloadHandlerAudioClip.GetContent(request);
                    });
                }

                internal static Task<T> OnMain<T>(Func<Task<T>> action)
                {
                    TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
                    MainThreadDispatcher.RunOnMainThread(async () =>
                    {
                        try
                        {
                            completion.SetResult(await action());
                        }
                        catch (Exception ex)
                        {
                            completion.SetException(ex);
                        }
                    });
                    return completion.Task;
                }

                internal void StopRule(string ruleId, bool notify, string modeToken = null)
                {
                    string resolvedRuleId = !string.IsNullOrWhiteSpace(ruleId)
                        ? ruleId
                        : m_RuleId;
                    unchecked
                    {
                        m_RuleToken++;
                    }

                    if (m_RuleSource != null)
                    {
                        AudioManager audioManager = AudioManager.instance;
                        if (audioManager != null)
                        {
                            audioManager.StopExclusiveUISound(m_RuleSource);
                        }
                        else
                        {
                            AudioManager.AudioSourcePool.Release(m_RuleSource);
                        }

                        m_RuleSource = null;
                    }

                    ReleaseRule();
                    m_RuleId = string.Empty;

                    if (notify && !string.IsNullOrWhiteSpace(resolvedRuleId))
                    {
                        NotifyRule(modeToken, resolvedRuleId, "stopped", string.Empty);
                    }
                }

                internal void Stop()
                {
                    StopAsset(m_AssetName, notify: true);
                    StopRule(m_RuleId, notify: true);
                    m_Announcements.Clear();
                }

                internal void RuleSource()
                {
                    if (m_RuleSource != null)
                    {
                        return;
                    }

                    m_RuleSource = AudioManager.AudioSourcePool.Get();
                    m_RuleSource.outputAudioMixerGroup = UiGroup();
                    m_RuleSource.dopplerLevel = 0f;
                    m_RuleSource.playOnAwake = false;
                    m_RuleSource.spatialBlend = 0f;
                    m_RuleSource.ignoreListenerPause = true;
                    m_RuleSource.pitch = 1f;
                    m_RuleSource.volume = VolumeScalar(DraftVol);
                }

                internal void ReleaseRule()
                {
                    if (m_RuleClip == null)
                    {
                        return;
                    }

                    UnityEngine.Object.Destroy(m_RuleClip);
                    m_RuleClip = null;
                }

                internal static void DestroyClip(AudioClip clip)
                {
                    if (clip == null)
                    {
                        return;
                    }

                    UnityEngine.Object.Destroy(clip);
                }

                internal void NotifyRule(string ruleId, string state, string error)
                {
                    NotifyRule(CurrentScope.Token, ruleId, state, error);
                }

                internal void NotifyRule(string modeToken, string ruleId, string state, string error)
                {
                    global::RapidTransitMod.Workbenches.UiEvents.Push(new BroadcastWorkbenchRulePreviewStateDto
                    {
                        mode = modeToken ?? CurrentScope.Token,
                        ruleId = ruleId ?? string.Empty,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty
                    });
                }

                internal bool Context(string lineId, out TriggerContext context)
                {
                    context = default;
                    WorkbenchLineRuntime runtime = m_Ctx.Snapshot.LineRuntime(lineId);
                    if (runtime == null)
                    {
                        return false;
                    }

                    List<StationGroup> stationGroups;
                    m_Ctx.Drafts.EnsureLine(lineId, runtime.Entity, out stationGroups);
                    if (stationGroups.Count == 0)
                    {
                        return false;
                    }

                    StationGroup currentStation = stationGroups[0];
                    StationGroup nextStation = stationGroups.Count > 1 ? stationGroups[1] : null;
                    StationGroup terminalStation = stationGroups[0];
                    StationGroup turnbackStation =
                        m_Ctx.Snapshot.TryTurnback(runtime.Entity, stationGroups, out StationGroup resolvedTurnbackStation)
                            ? resolvedTurnbackStation
                            : null;
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                        m_Ctx.Bindings.Draft(lineId);
                    List<BroadcastWorkbenchStationBindingDto> currentStationBindings = Broadcasting.Stations.Bindings(lineBindings, currentStation?.Representative?.id);
                    List<BroadcastWorkbenchStationBindingDto> nextStationBindings = Broadcasting.Stations.Bindings(lineBindings, nextStation?.Representative?.id);
                    List<BroadcastWorkbenchStationBindingDto> terminalStationBindings = Broadcasting.Stations.Bindings(lineBindings, terminalStation?.Representative?.id);
                    List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings = Broadcasting.Stations.Bindings(lineBindings, turnbackStation?.Representative?.id);
                    context = new TriggerContext(
                        lineId,
                        Entity.Null,
                        currentStation?.Representative?.name ?? string.Empty,
                        nextStation?.Representative?.name ?? string.Empty,
                        terminalStation?.Representative?.name ?? string.Empty,
                        turnbackStation?.Representative?.name ?? string.Empty,
                        Broadcasting.Stations.AssetName(currentStationBindings, 1),
                        Broadcasting.Stations.AssetName(nextStationBindings, 1),
                        Broadcasting.Stations.AssetName(terminalStationBindings, 1),
                        Broadcasting.Stations.AssetName(turnbackStationBindings, 1),
                        currentStationBindings,
                        nextStationBindings,
                        terminalStationBindings,
                        turnbackStationBindings);
                    return true;
                }

                internal void ApplyVolume()
                {
                    float volume = VolumeScalar(DraftVol);
                    if (m_Source != null)
                    {
                        m_Source.volume = volume;
                    }

                    if (m_RuleSource != null)
                    {
                        m_RuleSource.volume = volume;
                    }
                }

                internal static float VolumeScalar(int volumePercent)
                {
                    float progress = Clamp(volumePercent) / 100f;
                    return Mathf.Lerp(VolumeScalarMin, VolumeScalarMax, progress);
                }

                internal static int Clamp(int volumePercent)
                {
                    return Mathf.Clamp(volumePercent, 0, 100);
                }

                internal static int ParseVolume(string volumeJson, int fallback)
                {
                    if (!int.TryParse(volumeJson ?? string.Empty, out int parsed))
                    {
                        return Clamp(fallback);
                    }

                    return Clamp(parsed);
                }

                internal void NotifyAsset(string assetName, string state, string error)
                {
                    NotifyAsset(CurrentScope.Token, assetName, state, error);
                }

                internal void NotifyAsset(string modeToken, string assetName, string state, string error)
                {
                    global::RapidTransitMod.Workbenches.UiEvents.Push(new BroadcastWorkbenchAssetPreviewStateDto
                    {
                        mode = modeToken ?? CurrentScope.Token,
                        assetName = assetName ?? string.Empty,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty
                    });
                }

                internal static UnityWebRequest Request(string path, AudioType audioType)
                {
                    if (path.StartsWith("//?/", StringComparison.Ordinal))
                    {
                        return UnityWebRequestMultimedia.GetAudioClip("file://" + path.Replace("/", "\\"), audioType);
                    }

                    return UnityWebRequestMultimedia.GetAudioClip(new Uri("file://" + path), audioType);
                }

                internal static async Task WaitUnityWebRequest(UnityWebRequest request)
                {
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }
                }

                internal static AudioType AudioType(string filePath)
                {
                    switch ((IoPath.GetExtension(filePath) ?? string.Empty).ToLowerInvariant())
                    {
                        case ".ogg":
                            return UnityEngine.AudioType.OGGVORBIS;
                        case ".wav":
                            return UnityEngine.AudioType.WAV;
                        case ".mp3":
                            return UnityEngine.AudioType.MPEG;
                        default:
                            return UnityEngine.AudioType.UNKNOWN;
                    }
                }
    }
}
