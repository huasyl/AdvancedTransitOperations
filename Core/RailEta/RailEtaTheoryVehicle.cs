using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using TrainFlags = Game.Vehicles.TrainFlags;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal static class RailEtaTheoryVehicle
    {
        private readonly struct UnitSpec
        {
            public UnitSpec(Entity prefab, TrainFlags flags)
            {
                Prefab = prefab;
                Flags = flags;
            }

            public Entity Prefab { get; }
            public TrainFlags Flags { get; }
        }

        private readonly struct UnitPlacement
        {
            public UnitPlacement(float3 position, quaternion rotation)
            {
                Position = position;
                Rotation = rotation;
            }

            public float3 Position { get; }
            public quaternion Rotation { get; }
        }

        internal static bool Append(EntityManager entities, RailEtaScopedStaging staging, RailEtaRequestDescriptor descriptor,
            RailTravel.Path path, out string failure)
        {
            failure = string.Empty;
            if (path == null || path.IsEmpty || path.Segments.Length == 0)
            {
                failure = "Theory selected path is empty.";
                return false;
            }
            Entity line = RailEtaEntityId.ToEntity(descriptor);
            Entity target = RailEtaEntityId.ToEntity(descriptor.TargetCheckpointId);
            Entity primary = new Entity { Index = descriptor.ModelIndex, Version = descriptor.ModelVersion };
            Entity secondary = new Entity { Index = descriptor.SecondaryModelIndex, Version = descriptor.SecondaryModelVersion };
            RailTravel.Segment first = path.Segments[0];
            if (!TryLane(staging, line, first.LaneEntity, out RailEtaScopedLaneRow lane))
            {
                failure = "Theory selected path first lane is unavailable.";
                return false;
            }
            float fraction = first.TargetDelta.x;
            float3 frontPosition = Position(lane, fraction);
            float sign = first.TargetDelta.y >= fraction ? 1f : -1f;
            float3 direction = math.normalizesafe(Direction(lane, fraction) * sign, new float3(0f, 0f, 1f));
            var units = new List<UnitSpec>();
            if (!BuildUnits(entities, primary, secondary, units, out failure))
                return false;
            var placements = new List<UnitPlacement>(units.Count);
            BuildPlacements(entities, units, frontPosition, direction, placements);
            TrainData primaryData = entities.GetComponentData<TrainData>(primary);
            float maximumSpeed = float.MaxValue;
            float acceleration = float.MaxValue;
            float braking = float.MaxValue;
            for (int i = 0; i < units.Count; i++)
            {
                UnitSpec unit = units[i];
                if (!AppendUnit(entities, staging, line, unit, i, first, frontPosition, direction, placements[i],
                    out TrainData unitData, out failure))
                    return false;
                maximumSpeed = math.min(maximumSpeed, unitData.m_MaxSpeed);
                acceleration = math.min(acceleration, unitData.m_Acceleration);
                braking = math.min(braking, unitData.m_Braking);
            }
            staging.Vehicles.Add(new RailEtaScopedVehicleRow
            {
                ControllerOrdinal = 0,
                Controller = line,
                Target = target,
                Route = line,
                TargetSegmentIndex = 0,
                UnitCount = units.Count,
                MaximumSpeed = maximumSpeed,
                Acceleration = acceleration,
                Braking = braking,
                TurningLow = primaryData.m_Turning.x,
                TurningHigh = primaryData.m_Turning.y,
                VehiclePriority = VehicleUtils.GetPriority(primaryData),
                IsPassenger = 1,
                PathfindMaximumSpeed = primaryData.m_MaxSpeed,
                TrackTypes = (uint)primaryData.m_TrackType,
                PathfindFlags = (uint)(PathfindFlags.Stable | PathfindFlags.IgnoreFlow | PathfindFlags.IgnoreExtraEndAccessRequirements),
                PathElementIndex = 0,
                PathState = 0,
                PathDestination = target,
                HasPathInformation = 1,
                FrontLane = first.LaneEntity,
                RearLane = first.LaneEntity,
                FrontCacheLane = first.LaneEntity,
                RearCacheLane = first.LaneEntity,
                FrontCurveStart = fraction,
                FrontCurveEnd = fraction,
                RearCurvePosition = fraction,
                FrontCurvePosition = new float4(fraction),
                RearCurvePositions = new float4(fraction),
                FrontCacheCurvePosition = new float2(fraction),
                RearCacheCurvePosition = new float2(fraction)
            });
            return true;
        }

        internal static bool TryGetModelSignature(EntityManager entities, Entity primary, Entity secondary, out ulong signature)
        {
            signature = 0;
            var units = new List<UnitSpec>();
            if (!BuildUnits(entities, primary, secondary, units, out _))
                return false;

            ulong hash = RailEtaTheorySignatures.Seed;
            hash = RailEtaTheorySignatures.Mix(hash, units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitSpec unit = units[i];
                hash = MixEntity(hash, unit.Prefab);
                hash = RailEtaTheorySignatures.Mix(hash, (int)unit.Flags);
                TrainData train = entities.GetComponentData<TrainData>(unit.Prefab);
                ObjectGeometryData geometry = entities.GetComponentData<ObjectGeometryData>(unit.Prefab);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_MaxSpeed);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_Acceleration);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_Braking);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_Turning.x);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_Turning.y);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_BogieOffsets.x);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_BogieOffsets.y);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_AttachOffsets.x);
                hash = RailEtaTheorySignatures.Mix(hash, train.m_AttachOffsets.y);
                hash = RailEtaTheorySignatures.Mix(hash, (int)train.m_TrackType);
                hash = RailEtaTheorySignatures.Mix(hash, (int)train.m_TrainFlags);
                hash = RailEtaTheorySignatures.Mix(hash, (int)train.m_EnergyType);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.min.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.min.y);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.min.z);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.max.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.max.y);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Bounds.max.z);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Size.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Size.y);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Size.z);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Pivot.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Pivot.y);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_Pivot.z);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_LegSize.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_LegSize.y);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_LegSize.z);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_LegOffset.x);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_LegOffset.y);
                hash = RailEtaTheorySignatures.Mix(hash, (int)geometry.m_Flags);
                hash = RailEtaTheorySignatures.Mix(hash, geometry.m_MinLod);
                hash = RailEtaTheorySignatures.Mix(hash, (int)geometry.m_Layers);
                hash = RailEtaTheorySignatures.Mix(hash, (int)geometry.m_SubObjectMask);
            }
            signature = hash;
            return signature != 0;
        }

        private static bool BuildUnits(EntityManager entities, Entity primary, Entity secondary,
            List<UnitSpec> units, out string failure)
        {
            failure = string.Empty;
            if (!HasPhysics(entities, primary))
            {
                failure = "Theory vehicle model physics are unavailable.";
                return false;
            }
            bool multiple = entities.HasComponent<MultipleUnitTrainData>(primary);
            if (multiple)
            {
                int repeats = EngineCount(entities, primary);
                units.Add(new UnitSpec(primary, default));
                int placeholders = 0;
                for (int i = 0; i < repeats; i++)
                {
                    if (i != 0) units.Add(new UnitSpec(primary, default));
                    if (!AppendCarriages(entities, primary, units, ref placeholders, out failure)) return false;
                }
                for (int i = 0; i < placeholders; i++) units.Add(new UnitSpec(primary, default));
                return true;
            }

            int primaryPlaceholders = 0;
            if (secondary != Entity.Null)
            {
                if (!HasPhysics(entities, secondary))
                {
                    failure = "Theory secondary vehicle model physics are unavailable.";
                    return false;
                }
                int repeats = EngineCount(entities, secondary);
                for (int i = 0; i < repeats; i++)
                {
                    units.Add(new UnitSpec(secondary, default));
                    if (!AppendCarriages(entities, secondary, units, ref primaryPlaceholders, out failure)) return false;
                }
            }
            units.Add(new UnitSpec(primary, default));
            primaryPlaceholders--;
            for (int i = 0; i < primaryPlaceholders; i++) units.Add(new UnitSpec(primary, default));
            return true;
        }

        private static bool AppendCarriages(EntityManager entities, Entity engine, List<UnitSpec> units,
            ref int placeholders, out string failure)
        {
            failure = string.Empty;
            if (!entities.HasBuffer<VehicleCarriageElement>(engine)) return true;
            DynamicBuffer<VehicleCarriageElement> carriages = entities.GetBuffer<VehicleCarriageElement>(engine, true);
            for (int i = 0; i < carriages.Length; i++)
            {
                VehicleCarriageElement carriage = carriages[i];
                int count = math.max(0, carriage.m_Count.x);
                if (carriage.m_Prefab == Entity.Null)
                {
                    placeholders += count;
                    continue;
                }
                if (!HasPhysics(entities, carriage.m_Prefab))
                {
                    failure = "Theory carriage model physics are unavailable.";
                    return false;
                }
                TrainFlags flags = carriage.m_Direction == VehicleCarriageDirection.Reversed
                    ? TrainFlags.Reversed
                    : default;
                for (int j = 0; j < count; j++) units.Add(new UnitSpec(carriage.m_Prefab, flags));
            }
            return true;
        }

        private static int EngineCount(EntityManager entities, Entity prefab)
        {
            return entities.HasComponent<TrainEngineData>(prefab)
                ? math.max(1, entities.GetComponentData<TrainEngineData>(prefab).m_Count.x)
                : 1;
        }

        private static bool HasPhysics(EntityManager entities, Entity prefab)
        {
            return prefab != Entity.Null && entities.Exists(prefab)
                && entities.HasComponent<TrainData>(prefab)
                && entities.HasComponent<ObjectGeometryData>(prefab);
        }

        private static bool AppendUnit(EntityManager entities, RailEtaScopedStaging staging, Entity line, UnitSpec unit, int ordinal,
            RailTravel.Segment first, float3 frontPosition, float3 direction, UnitPlacement placement,
            out TrainData data, out string failure)
        {
            data = default;
            failure = string.Empty;
            Entity prefab = unit.Prefab;
            if (!HasPhysics(entities, prefab))
            {
                failure = "Theory vehicle model physics are unavailable.";
                return false;
            }
            data = entities.GetComponentData<TrainData>(prefab);
            ObjectGeometryData geometry = entities.GetComponentData<ObjectGeometryData>(prefab);
            var navLane = new TrainNavigationLane
            {
                m_Lane = first.LaneEntity,
                m_CurvePosition = first.TargetDelta
            };
            var bogie = new TrainBogieLane(navLane);
            TrainCurrentLane current = default;
            current.m_Front = bogie;
            current.m_Rear = bogie;
            current.m_FrontCache = new TrainBogieCache(bogie);
            current.m_RearCache = new TrainBogieCache(bogie);
            TrainNavigation navigation = default;
            navigation.m_Front.m_Position = frontPosition;
            navigation.m_Front.m_Direction = direction;
            navigation.m_Rear.m_Position = frontPosition;
            navigation.m_Rear.m_Direction = direction;
            navigation.m_Speed = 0f;
            var transform = new Game.Objects.Transform
            {
                m_Position = placement.Position,
                m_Rotation = placement.Rotation
            };
            staging.Units.Add(new RailEtaScopedUnitRow
            {
                IsTheory = 1,
                LayoutOrdinal = ordinal,
                Controller = line,
                Unit = UnitId(line, ordinal),
                Prefab = prefab,
                Length = geometry.m_Size.z,
                FrontBogieOffset = data.m_BogieOffsets.x,
                RearBogieOffset = data.m_BogieOffsets.y,
                FrontAttachOffset = data.m_AttachOffsets.x,
                RearAttachOffset = data.m_AttachOffsets.y,
                PrefabTrainFlags = (uint)data.m_TrainFlags,
                EnergyTypes = (uint)data.m_EnergyType,
                TrackTypes = (uint)data.m_TrackType,
                TransformPosition = placement.Position,
                TransformRotation = transform.m_Rotation,
                Transform = transform,
                Moving = default,
                Train = new Train(unit.Flags),
                Navigation = navigation,
                CurrentLane = current,
                PrefabTrainData = data,
                PrefabGeometryData = geometry,
                HasPrefabTrainData = 1,
                HasPrefabGeometryData = 1
            });
            return true;
        }

        private static void BuildPlacements(EntityManager entities, List<UnitSpec> units,
            float3 frontPosition, float3 direction, List<UnitPlacement> placements)
        {
            for (int i = 0; i < units.Count; i++)
            {
                TrainData data = entities.GetComponentData<TrainData>(units[i].Prefab);
                bool reversed = (units[i].Flags & TrainFlags.Reversed) != 0;
                if (reversed)
                {
                    data.m_BogieOffsets = data.m_BogieOffsets.yx;
                }
                float3 position = frontPosition - direction * data.m_BogieOffsets.x;
                float3 facing = reversed ? -direction : direction;
                placements.Add(new UnitPlacement(position,
                    quaternion.LookRotationSafe(facing, new float3(0f, 1f, 0f))));
            }
        }

        private static Entity UnitId(Entity line, int ordinal)
        {
            int version = line.Index ^ line.Version;
            return new Entity { Index = -1 - ordinal, Version = version == 0 ? 1 : version };
        }

        private static ulong MixEntity(ulong hash, Entity entity)
        {
            hash = RailEtaTheorySignatures.Mix(hash, entity.Index);
            return RailEtaTheorySignatures.Mix(hash, entity.Version);
        }

        private static bool TryLane(RailEtaScopedStaging staging, Entity controller, Entity lane,
            out RailEtaScopedLaneRow result)
        {
            NativeArray<RailEtaScopedLaneRow> rows = staging.Lanes.AsArray();
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Controller != controller || rows[i].Lane != lane || rows[i].Source != 7) continue;
                result = rows[i];
                return true;
            }
            result = default;
            return false;
        }

        private static float3 Position(RailEtaScopedLaneRow lane, float t)
        {
            float u = 1f - t;
            return u * u * u * lane.CurveA + 3f * u * u * t * lane.CurveB
                + 3f * u * t * t * lane.CurveC + t * t * t * lane.CurveD;
        }

        private static float3 Direction(RailEtaScopedLaneRow lane, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (lane.CurveB - lane.CurveA)
                + 6f * u * t * (lane.CurveC - lane.CurveB)
                + 3f * t * t * (lane.CurveD - lane.CurveC);
        }
    }
}
