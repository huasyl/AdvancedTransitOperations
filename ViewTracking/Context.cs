using System;
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
        private UnityEngine.Object m_Controller;
        private float m_NextConnectTime;
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
            if (m_SystemType != null)
                return;
            Type system = assembly.GetType("FirstPersonCameraContinued.Systems.FirstPersonCameraSystem", false);
            if (system == null)
                return;
            m_SystemType = system;
            m_ControllerType = assembly.GetType("FirstPersonCameraContinued.MonoBehaviours.FirstPersonCameraController", false);
            m_EntryInfo = system.GetProperty("EntryInfo", BindingFlags.Public | BindingFlags.Instance);
            m_Activated = m_EntryInfo?.PropertyType.GetField("Activated", BindingFlags.Public | BindingFlags.Instance);
            m_FollowEntity = m_ControllerType?.GetMethod("GetFollowEntity", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        }

        internal FollowView ReadContext()
        {
            if (m_ReadFrame == Time.frameCount)
                return m_View;
            m_ReadFrame = Time.frameCount;
            m_View = default;
            if (!m_World.IsCreated)
                return m_View;
            try
            {
                if (m_SystemType != null)
                {
                    if (m_EntryInfo == null || m_Activated == null || m_FollowEntity == null)
                        return m_View;
                    m_System = m_World.GetExistingSystemManaged(m_SystemType);
                    if (m_System == null)
                        return m_View;
                    object entry = m_EntryInfo.GetValue(m_System);
                    if (entry == null)
                        return m_View;
                    if ((bool)m_Activated.GetValue(entry))
                    {
                        if (m_Controller == null && Time.realtimeSinceStartup >= m_NextConnectTime)
                        {
                            m_NextConnectTime = Time.realtimeSinceStartup + 1f;
                            m_Controller = UnityEngine.Object.FindObjectOfType(m_ControllerType);
                        }
                        if (m_Controller == null)
                            return m_View;
                        Entity followed = (Entity)m_FollowEntity.Invoke(m_Controller, null);
                        return m_View = new FollowView(FollowMode.FirstPerson, followed, m_ResolveVehicle(followed));
                    }
                }
                OrbitCameraController orbit = m_Camera?.orbitCameraController;
                if (orbit != null && ReferenceEquals(m_Camera.activeCameraController, orbit))
                {
                    Entity followed = orbit.followedEntity;
                    m_View = new FollowView(FollowMode.Standard, followed, m_ResolveVehicle(followed));
                }
            }
            catch
            {
                m_View = default;
            }
            return m_View;
        }

        internal Entity ReadFollowedVehicle() => ReadContext().Vehicle;

        internal void ResetConnection()
        {
            m_System = null;
            m_Controller = null;
            m_NextConnectTime = 0f;
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
