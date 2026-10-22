using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Rendering;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.ViewTracking
{
    internal enum FollowMode { None, Standard, FirstPerson }

    internal readonly struct FollowView
    {
        internal readonly FollowMode Mode;
        internal readonly Entity FollowedEntity;
        internal readonly Entity Vehicle;

        internal FollowView(FollowMode mode, Entity followedEntity, Entity vehicle)
        {
            Mode = mode;
            FollowedEntity = followedEntity;
            Vehicle = vehicle;
        }
    }

    internal sealed class FollowContext : IDisposable
    {
        private readonly World m_World;
        private readonly CameraUpdateSystem m_Camera;
        private readonly Func<Entity, Entity> m_ResolveVehicle;
        private Type m_SystemType;
        private Type m_ControllerType;
        private PropertyInfo m_EntryInfo;
        private FieldInfo m_Activated;
        private MethodInfo m_FollowEntity;
        private ComponentSystemBase m_System;
        private object m_Entry;
        private UnityEngine.Object m_Controller;
        private int m_ReadFrame = -1;
        private FollowView m_View;

        internal FollowContext(World world, CameraUpdateSystem camera, Func<Entity, Entity> resolveVehicle)
        {
            m_World = world;
            m_Camera = camera;
            m_ResolveVehicle = resolveVehicle;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                Discover(assembly);
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Discover(args.LoadedAssembly);

        private void Discover(Assembly assembly)
        {
            try
            {
                if (m_SystemType != null)
                    return;
                Type system = assembly.GetType("FirstPersonCameraContinued.Systems.FirstPersonCameraSystem", false);
                if (system == null)
                    return;
                Type controller = assembly.GetType("FirstPersonCameraContinued.MonoBehaviours.FirstPersonCameraController", false);
                PropertyInfo entryInfo = system.GetProperty("EntryInfo", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo activated = entryInfo?.PropertyType.GetField("Activated", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo followEntity = controller?.GetMethod("GetFollowEntity", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                List<string> invalid = new List<string>();
                if (controller == null)
                    invalid.Add("FirstPersonCameraController");
                if (entryInfo == null)
                    invalid.Add("EntryInfo");
                if (activated?.FieldType != typeof(bool))
                    invalid.Add("EntryInfo.Activated");
                if (followEntity?.ReturnType != typeof(Entity))
                    invalid.Add("GetFollowEntity");
                if (invalid.Count > 0)
                {
                    Mod.log.Info("[FirstPerson] First Person Camera Continued API mismatch: " + string.Join(", ", invalid) + "; first-person integration disabled");
                    return;
                }
                m_ControllerType = controller;
                m_EntryInfo = entryInfo;
                m_Activated = activated;
                m_FollowEntity = followEntity;
                m_SystemType = system;
                Mod.log.Info("[FirstPerson] Connected to First Person Camera Continued");
            }
            catch (Exception ex)
            {
                Mod.log.Info("[FirstPerson] Failed to inspect assembly " + assembly.GetName().Name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal FollowView ReadContext()
        {
            if (m_ReadFrame == Time.frameCount)
                return m_View;
            m_ReadFrame = Time.frameCount;
            m_View = default;
            if (!m_World.IsCreated)
                return m_View;
            if (TryReadFirstPerson(out Entity firstPerson))
                return m_View = new FollowView(FollowMode.FirstPerson, firstPerson, m_ResolveVehicle(firstPerson));
            OrbitCameraController orbit = m_Camera?.orbitCameraController;
            if (orbit != null && ReferenceEquals(m_Camera.activeCameraController, orbit))
            {
                Entity followed = orbit.followedEntity;
                m_View = new FollowView(FollowMode.Standard, followed, m_ResolveVehicle(followed));
            }
            return m_View;
        }

        private bool TryReadFirstPerson(out Entity followed)
        {
            followed = Entity.Null;
            if (m_SystemType == null)
                return false;
            try
            {
                if (m_System == null)
                    m_System = m_World.GetExistingSystemManaged(m_SystemType);
                if (m_System == null)
                    return false;
                if (m_Entry == null)
                    m_Entry = m_EntryInfo.GetValue(m_System);
                if (m_Entry == null)
                    return false;
                if (!(bool)m_Activated.GetValue(m_Entry))
                    return false;
                if (m_Controller == null)
                    m_Controller = UnityEngine.Object.FindObjectOfType(m_ControllerType);
                if (m_Controller == null)
                {
                    DisableFirstPerson("First Person Camera Continued controller not found");
                    return false;
                }
                followed = (Entity)m_FollowEntity.Invoke(m_Controller, null);
                return true;
            }
            catch (Exception ex)
            {
                Exception cause = ex.GetBaseException();
                DisableFirstPerson("Failed to read First Person Camera Continued state: " + cause.GetType().Name + ": " + cause.Message);
                return false;
            }
        }

        private void DisableFirstPerson(string reason)
        {
            Mod.log.Info("[FirstPerson] " + reason + "; first-person integration disabled");
            m_SystemType = null;
            m_System = null;
            m_Entry = null;
            m_Controller = null;
        }

        internal Entity ReadFollowedVehicle() => ReadContext().Vehicle;

        internal void ResetConnection()
        {
            m_System = null;
            m_Entry = null;
            m_Controller = null;
            m_ReadFrame = -1;
            m_View = default;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            m_System = null;
            m_Controller = null;
            m_View = default;
        }
    }
}
