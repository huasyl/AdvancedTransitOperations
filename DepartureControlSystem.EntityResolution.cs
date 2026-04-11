using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private Entity ResolveVehicleLine(Entity vehicle)
        {
            if (m_VehicleLine.TryGetValue(vehicle, out Entity mappedLine) && mappedLine != Entity.Null)
                return mappedLine;

            if (EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                if (route != Entity.Null && EntityManager.HasComponent<TransportLine>(route))
                    return route;
            }

            return Entity.Null;
        }

        private Entity ResolvePassingStationBuilding(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;
            if (EntityManager.HasComponent<Building>(entity))
            {
                if (IsSelectableTransitStationBuilding(entity))
                    return entity;
                return Entity.Null;
            }

            Entity stopEntity = ResolveWorkbenchStopEntity(entity);
            if (stopEntity != Entity.Null)
            {
                Entity stationEntity = FindTransportStationFromStop(stopEntity);
                if (stationEntity != Entity.Null && IsSelectableTransitStationBuilding(stationEntity))
                    return stationEntity;
            }

            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Building>(current) && IsSelectableTransitStationBuilding(current))
                    return current;
                if (!EntityManager.HasComponent<Owner>(current))
                    break;
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }


        private Entity ResolveSelectedLineEntity(Entity entity)
        {
            return ResolveSelectedLineEntity(entity, Entity.Null);
        }

        private Entity ResolveSelectedLineEntity(Entity entity, Entity preferredRoute)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;

            if (preferredRoute != Entity.Null
                && EntityManager.Exists(preferredRoute)
                && EntityManager.HasComponent<TransportLine>(preferredRoute))
            {
                return preferredRoute;
            }

            if (EntityManager.HasComponent<TransportLine>(entity))
                return entity;

            if (EntityManager.HasComponent<CurrentRoute>(entity))
            {
                Entity currentRoute = EntityManager.GetComponentData<CurrentRoute>(entity).m_Route;
                if (currentRoute != Entity.Null && EntityManager.HasComponent<TransportLine>(currentRoute))
                    return currentRoute;
            }

            var routes = new List<Entity>(4);
            TryGetStationRoutes(entity, routes);
            if (routes.Count == 0)
                TryGetStopRoutes(entity, routes);

            Entity bestRoute = Entity.Null;
            for (int i = 0; i < routes.Count; i++)
            {
                Entity route = routes[i];
                if (route == Entity.Null || !EntityManager.Exists(route) || !EntityManager.HasComponent<TransportLine>(route))
                    continue;
                if (bestRoute == Entity.Null || route.Index < bestRoute.Index)
                    bestRoute = route;
            }

            return bestRoute;
        }

        private bool TryGetStationRoutes(Entity entity, List<Entity> routes)
        {
            bool found = false;
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                for (int i = 0; i < subObjects.Length; i++)
                    found |= TryGetStopRoutes(subObjects[i].m_SubObject, routes);
            }

            if (EntityManager.HasBuffer<InstalledUpgrade>(entity))
            {
                DynamicBuffer<InstalledUpgrade> upgrades = EntityManager.GetBuffer<InstalledUpgrade>(entity, true);
                for (int i = 0; i < upgrades.Length; i++)
                    found |= TryGetStationRoutes(upgrades[i].m_Upgrade, routes);
            }

            return found;
        }

        private bool TryGetStopRoutes(Entity entity, List<Entity> routes)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return false;
            if (!EntityManager.HasBuffer<ConnectedRoute>(entity))
                return false;
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(entity))
                return false;
            if (EntityManager.HasComponent<TaxiStand>(entity))
                return false;

            DynamicBuffer<ConnectedRoute> connectedRoutes = EntityManager.GetBuffer<ConnectedRoute>(entity, true);
            bool found = false;
            for (int i = 0; i < connectedRoutes.Length; i++)
            {
                ConnectedRoute connectedRoute = connectedRoutes[i];
                if (!EntityManager.HasComponent<Owner>(connectedRoute.m_Waypoint))
                    continue;
                Entity owner = EntityManager.GetComponentData<Owner>(connectedRoute.m_Waypoint).m_Owner;
                if (owner == Entity.Null || !EntityManager.HasComponent<TransportLine>(owner))
                    continue;
                if (!routes.Contains(owner))
                    routes.Add(owner);
                found = true;
            }

            return found;
        }

        private bool IsSelectableTransitStationBuilding(Entity building)
        {
            if (building == Entity.Null || !EntityManager.Exists(building) || !EntityManager.HasComponent<Building>(building))
                return false;

            var routes = new List<Entity>(4);
            return TryGetStationRoutes(building, routes) && routes.Count > 0;
        }

        private Entity ResolveSelectedVehicleEntity(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;

            Entity original = entity;
            Entity current = entity;
            Entity fallbackVehicle = Entity.Null;
            int guard = 0;
            while (current != Entity.Null && EntityManager.Exists(current) && guard++ < 16)
            {
                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                    fallbackVehicle = current;

                if (m_VehicleState.ContainsKey(current))
                    return current;

                if (EntityManager.HasComponent<Controller>(current))
                {
                    Entity controller = EntityManager.GetComponentData<Controller>(current).m_Controller;
                    if (controller != Entity.Null && controller != current)
                    {
                        current = controller;
                        continue;
                    }
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            Entity layoutResolved = ResolveManagedVehicleFromLayout(original, fallbackVehicle);
            if (layoutResolved != Entity.Null)
                return layoutResolved;

            return fallbackVehicle;
        }

        private Entity ResolveManagedVehicleFromLayout(Entity original, Entity fallbackVehicle)
        {
            if (m_VehicleState.Count == 0)
                return Entity.Null;

            var managedVehicles = m_VehicleState.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < managedVehicles.Length; i++)
                {
                    Entity managedVehicle = managedVehicles[i];
                    if (!EntityManager.Exists(managedVehicle) || !EntityManager.HasBuffer<LayoutElement>(managedVehicle))
                        continue;

                    DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(managedVehicle, true);
                    for (int j = 0; j < layout.Length; j++)
                    {
                        Entity layoutVehicle = layout[j].m_Vehicle;
                        if (layoutVehicle == original || (fallbackVehicle != Entity.Null && layoutVehicle == fallbackVehicle))
                            return managedVehicle;
                    }
                }
            }
            finally
            {
                managedVehicles.Dispose();
            }

            return Entity.Null;
        }

        private Entity ResolveRuntimeControllerVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return Entity.Null;

            Entity current = vehicle;
            Entity fallbackVehicle = Entity.Null;
            int guard = 0;
            while (current != Entity.Null && EntityManager.Exists(current) && guard++ < 16)
            {
                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                    fallbackVehicle = current;

                if (EntityManager.HasBuffer<LayoutElement>(current)
                    && EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                {
                    return current;
                }

                if (EntityManager.HasComponent<Controller>(current))
                {
                    Entity controller = EntityManager.GetComponentData<Controller>(current).m_Controller;
                    if (controller != Entity.Null && controller != current)
                    {
                        current = controller;
                        continue;
                    }
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return fallbackVehicle;
        }


    }
}
