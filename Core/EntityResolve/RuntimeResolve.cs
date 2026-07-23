using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Workbench;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeResolve
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public RuntimeResolve(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public Entity Line(Entity vehicle)
        {
            if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) && mappedLine != Entity.Null)
                return mappedLine;

            if (m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                Entity route = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                if (route != Entity.Null && m_Runtime.EntityManager.HasComponent<TransportLine>(route))
                    return route;
            }

            return Entity.Null;
        }

        public Entity PassingStation(Entity entity)
        {
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return Entity.Null;
            if (m_Runtime.EntityManager.HasComponent<Building>(entity))
            {
                if (SelectableStation(entity))
                    return entity;
                return Entity.Null;
            }

            Entity stopEntity = Stop(entity);
            if (stopEntity != Entity.Null)
            {
                Entity stationEntity = StationOf(stopEntity);
                if (stationEntity != Entity.Null && SelectableStation(stationEntity))
                    return stationEntity;
            }

            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (m_Runtime.EntityManager.HasComponent<Building>(current) && SelectableStation(current))
                    return current;
                if (!m_Runtime.EntityManager.HasComponent<Owner>(current))
                    break;
                current = m_Runtime.EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        public Entity SelectedLine(Entity entity, Entity preferredRoute)
        {
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return Entity.Null;

            if (m_Runtime.EntityManager.HasComponent<TransportLine>(entity))
                return entity;

            if (m_Runtime.EntityManager.HasComponent<CurrentRoute>(entity))
            {
                Entity currentRoute = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(entity).m_Route;
                if (currentRoute != Entity.Null && m_Runtime.EntityManager.HasComponent<TransportLine>(currentRoute))
                    return currentRoute;
            }

            var routes = new List<Entity>(4);
            CollectSelectionRoutes(entity, routes);

            if (IsTransportLine(preferredRoute) && PreferredRouteMatchesSelection(entity, preferredRoute, routes))
                return preferredRoute;

            Entity bestRoute = Entity.Null;
            for (int i = 0; i < routes.Count; i++)
            {
                Entity route = routes[i];
                if (route == Entity.Null
                    || !m_Runtime.EntityManager.Exists(route)
                    || !m_Runtime.EntityManager.HasComponent<TransportLine>(route))
                {
                    continue;
                }
                if (bestRoute == Entity.Null || route.Index < bestRoute.Index)
                    bestRoute = route;
            }

            return bestRoute;
        }

        public Entity SelectedLine(Entity entity)
        {
            return SelectedLine(entity, Entity.Null);
        }

        public Entity SelectedVehicle(Entity entity)
        {
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return Entity.Null;

            Entity original = entity;
            Entity current = entity;
            Entity fallbackVehicle = Entity.Null;
            int guard = 0;
            while (current != Entity.Null && m_Runtime.EntityManager.Exists(current) && guard++ < 16)
            {
                if (m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                    fallbackVehicle = current;

                if (m_Runtime.m_VehicleView.Contains(current))
                    return current;

                if (m_Runtime.EntityManager.HasComponent<Controller>(current))
                {
                    Entity controller = m_Runtime.EntityManager.GetComponentData<Controller>(current).m_Controller;
                    if (controller != Entity.Null && controller != current)
                    {
                        current = controller;
                        continue;
                    }
                }

                if (!m_Runtime.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = m_Runtime.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            Entity layoutResolved = ManagedFromLayout(original, fallbackVehicle);
            if (layoutResolved != Entity.Null)
                return layoutResolved;

            return fallbackVehicle;
        }

        public Entity RuntimeVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return Entity.Null;

            Entity current = vehicle;
            Entity fallbackVehicle = Entity.Null;
            int guard = 0;
            while (current != Entity.Null && m_Runtime.EntityManager.Exists(current) && guard++ < 16)
            {
                if (m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                    fallbackVehicle = current;

                if (m_Runtime.EntityManager.HasBuffer<LayoutElement>(current)
                    && m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                {
                    return current;
                }

                if (m_Runtime.EntityManager.HasComponent<Controller>(current))
                {
                    Entity controller = m_Runtime.EntityManager.GetComponentData<Controller>(current).m_Controller;
                    if (controller != Entity.Null && controller != current)
                    {
                        current = controller;
                        continue;
                    }
                }

                if (!m_Runtime.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = m_Runtime.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return fallbackVehicle;
        }

        public Entity Stop(Entity waypoint)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Stop(waypoint);
        }

        public Entity Anchor(Entity waypoint)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Anchor(waypoint);
        }

        public Entity AnchorFromStop(Entity stopEntity)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().AnchorOf(stopEntity);
        }

        public string Sak(Entity anchor)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Key(anchor);
        }

        public string EnsureSak(Entity anchor)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().EnsureKey(anchor);
        }

        public StopRef StopRef(Entity waypoint)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Ref(waypoint);
        }

        public StopRef StopRef(Entity waypoint, StopRef fallback)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Ref(waypoint, fallback);
        }

        public string StopId(Entity entity, ResolvedStopKind kind)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Id(entity, kind);
        }

        public string StationName(Entity stopEntity)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().StationName(stopEntity);
        }

        public Entity StationOf(Entity stop)
        {
            return m_Runtime.m_WorkbenchBridge.StopSvc().Station(stop);
        }

        public List<DispatchWorkbenchStationDto> Stations(Entity line)
        {
            return m_Runtime.m_WorkbenchBridge.Catalog().Stations(line);
        }

        public void Origin(Entity line, out string originStationId, out string originStationName)
        {
            m_Runtime.m_WorkbenchBridge.Catalog().LineOrigin(line, out originStationId, out originStationName);
        }

        public string StationId(int order)
        {
            return Catalog.StationId(order);
        }

        private bool TryStationRoutes(Entity entity, List<Entity> routes)
        {
            bool found = false;
            if (m_Runtime.EntityManager.HasBuffer<Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects = m_Runtime.EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                for (int i = 0; i < subObjects.Length; i++)
                    found |= TryStopRoutes(subObjects[i].m_SubObject, routes);
            }

            if (m_Runtime.EntityManager.HasBuffer<InstalledUpgrade>(entity))
            {
                DynamicBuffer<InstalledUpgrade> upgrades = m_Runtime.EntityManager.GetBuffer<InstalledUpgrade>(entity, true);
                for (int i = 0; i < upgrades.Length; i++)
                    found |= TryStationRoutes(upgrades[i].m_Upgrade, routes);
            }

            return found;
        }

        private void CollectSelectionRoutes(Entity entity, List<Entity> routes)
        {
            TryStationRoutes(entity, routes);
            if (routes.Count != 0)
                return;

            TryStopRoutes(entity, routes);
            if (routes.Count != 0)
                return;

            Entity stop = Stop(entity);
            if (stop != Entity.Null && stop != entity)
            {
                TryStopRoutes(stop, routes);
                if (routes.Count != 0)
                    return;

                Entity station = StationOf(stop);
                if (station != Entity.Null && station != entity)
                {
                    TryStationRoutes(station, routes);
                    if (routes.Count != 0)
                        return;
                }
            }

            Entity passingStation = PassingStation(entity);
            if (passingStation != Entity.Null && passingStation != entity)
                TryStationRoutes(passingStation, routes);
        }

        private bool PreferredRouteMatchesSelection(Entity entity, Entity preferredRoute, List<Entity> routes)
        {
            if (routes.Contains(preferredRoute))
                return true;

            if (!m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(preferredRoute))
                return false;

            Entity selectedStop = Stop(entity);
            Entity selectedStation = selectedStop != Entity.Null ? StationOf(selectedStop) : Entity.Null;
            if (selectedStation == Entity.Null && m_Runtime.EntityManager.HasComponent<Building>(entity))
                selectedStation = entity;
            if (selectedStation == Entity.Null)
                selectedStation = PassingStation(entity);

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(preferredRoute, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == entity)
                    return true;

                Entity routeStop = Stop(waypoint);
                if (routeStop != Entity.Null && routeStop == selectedStop)
                    return true;

                if (selectedStation == Entity.Null)
                    continue;

                Entity routeStation = routeStop != Entity.Null ? StationOf(routeStop) : PassingStation(waypoint);
                if (routeStation != Entity.Null && routeStation == selectedStation)
                    return true;
            }

            return false;
        }

        private bool TryStopRoutes(Entity entity, List<Entity> routes)
        {
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return false;
            if (!m_Runtime.EntityManager.HasBuffer<ConnectedRoute>(entity))
                return false;
            if (!m_Runtime.EntityManager.HasComponent<Game.Routes.TransportStop>(entity))
                return false;
            if (m_Runtime.EntityManager.HasComponent<TaxiStand>(entity))
                return false;

            DynamicBuffer<ConnectedRoute> connectedRoutes = m_Runtime.EntityManager.GetBuffer<ConnectedRoute>(entity, true);
            bool found = false;
            for (int i = 0; i < connectedRoutes.Length; i++)
            {
                ConnectedRoute connectedRoute = connectedRoutes[i];
                if (!m_Runtime.EntityManager.HasComponent<Owner>(connectedRoute.m_Waypoint))
                    continue;
                Entity owner = m_Runtime.EntityManager.GetComponentData<Owner>(connectedRoute.m_Waypoint).m_Owner;
                if (owner == Entity.Null || !m_Runtime.EntityManager.HasComponent<TransportLine>(owner))
                    continue;
                if (!routes.Contains(owner))
                    routes.Add(owner);
                found = true;
            }

            return found;
        }

        private bool IsTransportLine(Entity entity)
        {
            return entity != Entity.Null
                && m_Runtime.EntityManager.Exists(entity)
                && m_Runtime.EntityManager.HasComponent<TransportLine>(entity);
        }

        private bool SelectableStation(Entity building)
        {
            if (building == Entity.Null
                || !m_Runtime.EntityManager.Exists(building)
                || !m_Runtime.EntityManager.HasComponent<Building>(building))
            {
                return false;
            }

            var routes = new List<Entity>(4);
            return TryStationRoutes(building, routes) && routes.Count > 0;
        }

        private Entity ManagedFromLayout(Entity original, Entity fallbackVehicle)
        {
            if (m_Runtime.m_VehicleView.Count == 0)
                return Entity.Null;

            var managedVehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < managedVehicles.Length; i++)
                {
                    Entity managedVehicle = managedVehicles[i];
                    if (!m_Runtime.EntityManager.Exists(managedVehicle)
                        || !m_Runtime.EntityManager.HasBuffer<LayoutElement>(managedVehicle))
                    {
                        continue;
                    }

                    DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(managedVehicle, true);
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
    }
}
