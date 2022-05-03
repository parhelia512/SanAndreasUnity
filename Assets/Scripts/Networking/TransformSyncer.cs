using UnityEngine;
using Mirror;
using SanAndreasUnity.Utilities;
using System.Collections.Generic;
using System.Linq;

namespace SanAndreasUnity.Net
{
    public class TransformSyncer
    {
        public enum ClientUpdateType
        {
            ConstantVelocity,
            Lerp,
            Slerp,
            SnapshotInterpolation,
        }

        [System.Serializable]
        public struct Parameters
        {
            public bool useSmoothDeltaTime;

            public ClientUpdateType clientUpdateType;

            public float constantVelocityMultiplier;

            public float lerpFactor;

            public bool useRigidBody;

            public bool visualize;

            public ushort maxNumVisualizations;

            public float visualizationScale;

            public float snapshotLatency;

            public static Parameters Default => new Parameters
            {
                useSmoothDeltaTime = true,
                clientUpdateType = ClientUpdateType.ConstantVelocity,
                constantVelocityMultiplier = 1f,
                lerpFactor = 30f,
                useRigidBody = true,
                visualize = false,
                maxNumVisualizations = 10,
                visualizationScale = 0.2f,
                snapshotLatency = 0.1f,
            };
        }

        private Parameters m_parameters = Parameters.Default;

        private SyncData m_currentSyncData = new SyncData { Rotation = Quaternion.identity };
        public SyncData CurrentSyncData => m_currentSyncData;

        // we will switch to this sync data when we reach the current sync data
        private SyncData? m_nextSyncData = null;

        private readonly Transform m_transform;
        public Transform Transform => m_transform;

        private readonly Rigidbody m_rigidbody;

        public struct SyncData
        {
            // sync data (as reported by server) toward which we move the transform
            public Vector3 Position;
            public Quaternion Rotation;

            // these are the velocities used to move the object, calculated when new server data arrives
            public float CalculatedVelocityMagnitude;
            public float CalculatedAngularVelocityMagnitude;

            // timestamp of server when this data was sent (batch timestamp)
            public double sentNetworkTime;

            // network time when this data was received
            public double receivedNetworkTime;

            public void Apply(Transform tr)
            {
                tr.localPosition = this.Position;
                tr.localRotation = this.Rotation;
            }
        }

        private readonly bool m_hasTransform = false;
        private readonly bool m_hasRigidBody = false;

        private readonly NetworkBehaviour m_networkBehaviour;

        private readonly Queue<GameObject> m_visualizationQueue = new Queue<GameObject>();

        private readonly Queue<SyncData> m_syncDataBuffer = new Queue<SyncData>();
        public int SnapshotBufferCount => m_syncDataBuffer.Count;
        


        public TransformSyncer(Transform tr, Parameters parameters, NetworkBehaviour networkBehaviour)
        {
            m_transform = tr;
            m_rigidbody = tr != null ? tr.GetComponent<Rigidbody>() : null;
            m_parameters = parameters;
            m_networkBehaviour = networkBehaviour;
            m_hasTransform = tr != null;
            m_hasRigidBody = m_rigidbody != null;
        }

        public bool OnSerialize(NetworkWriter writer, bool initialState)
        {
            byte flags = 0;
            writer.Write(flags);

            writer.Write(this.GetPosition());
            writer.Write(this.GetRotation().eulerAngles);

            return true;
        }

        public void OnDeserialize(NetworkReader reader, bool initialState)
        {
            byte flags = reader.ReadByte();

            var syncData = new SyncData();

            syncData.sentNetworkTime = NetworkClient.connection.remoteTimeStamp;
            syncData.receivedNetworkTime = NetworkTime.time;

            syncData.Position = reader.ReadVector3();// + syncData.velocity * this.syncInterval;
            syncData.Rotation = Quaternion.Euler(reader.ReadVector3());

            if (initialState)
            {
                syncData.CalculatedVelocityMagnitude = float.PositiveInfinity;
                syncData.CalculatedAngularVelocityMagnitude = float.PositiveInfinity;

                m_currentSyncData = syncData;

                this.WarpToLatestSyncData();
            }
            else
            {
                syncData.CalculatedVelocityMagnitude = (syncData.Position - m_currentSyncData.Position).magnitude / m_networkBehaviour.syncInterval;
                syncData.CalculatedAngularVelocityMagnitude = Quaternion.Angle(syncData.Rotation, m_currentSyncData.Rotation) / m_networkBehaviour.syncInterval;

                m_nextSyncData = syncData;

                this.AddToVisualization(syncData);
            }

            this.AddToBuffer(syncData);
        }

        void AddToVisualization(SyncData syncData)
        {
            if (!m_parameters.visualize || m_parameters.maxNumVisualizations <= 0)
            {
                while (m_visualizationQueue.Count > 0)
                    Object.Destroy(m_visualizationQueue.Dequeue());

                return;
            }

            while (m_visualizationQueue.Count >= m_parameters.maxNumVisualizations)
                Object.Destroy(m_visualizationQueue.Dequeue());

            var newGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            newGo.transform.SetPositionAndRotation(syncData.Position, syncData.Rotation);
            newGo.transform.localScale = Vector3.one * m_parameters.visualizationScale;
            Object.DestroyImmediate(newGo.GetComponent<Collider>());
            
            m_visualizationQueue.Enqueue(newGo);

            int i = 0;
            foreach (var go in m_visualizationQueue)
            {
                go.name = $"{m_networkBehaviour.name} - sync visualization {i}";
                go.GetComponent<Renderer>().material.color = Color.Lerp(Color.white, Color.black, i / (float)m_visualizationQueue.Count);
                i++;
            }
        }

        private void AddToBuffer(SyncData syncData)
        {
            if (m_parameters.clientUpdateType != ClientUpdateType.SnapshotInterpolation)
            {
                m_syncDataBuffer.Clear();
                return;
            }

            if (m_syncDataBuffer.Count == 0)
            {
                m_syncDataBuffer.Enqueue(syncData);
                return;
            }

            if (m_syncDataBuffer.Last().sentNetworkTime >= syncData.sentNetworkTime)
            {
                // can happen if packets arrive out of order (eg. on UDP transport)
                return;
            }

            m_syncDataBuffer.Enqueue(syncData);
        }

        public void Update()
        {
            if (!m_hasTransform)
                return;

            if (NetUtils.IsServer)
            {
                m_networkBehaviour.SetSyncVarDirtyBit(1);
            }
            else
            {
                this.CheckIfArrivedToNextSyncData();

                switch (m_parameters.clientUpdateType)
                {
                    case ClientUpdateType.ConstantVelocity:
                        this.UpdateClientUsingConstantVelocity();
                        break;
                    case ClientUpdateType.Lerp:
                        this.UpdateClientUsingLerp();
                        break;
                    case ClientUpdateType.Slerp:
                        this.UpdateClientUsingSphericalLerp();
                        break;
                    case ClientUpdateType.SnapshotInterpolation:
                        this.UpdateClientUsingSnapshotInterpolation();
                        break;
                    default:
                        break;
                }

                this.CheckIfArrivedToNextSyncData();
            }
        }

        private void UpdateClientUsingSnapshotInterpolation()
        {
            /*if (SnapshotInterpolation.Compute(
                    NetworkTime.localTime,
                    Time.deltaTime,
                    ref m_clientInterpolationTime,
                    m_networkBehaviour.syncInterval * 2f,
                    m_syncDataBuffer,
                    4,
                    0.1f,
                    DoSnapshotInterpolation,
                    out SyncData computed))
            {
                this.SetPosition(computed.Position);
                this.SetRotation(computed.Rotation);
            }*/

            if (m_syncDataBuffer.Count == 0)
                return;

            double currentNetworkTime = NetworkTime.time - m_parameters.snapshotLatency;

            SyncData syncDataOfHigher;
            SyncData syncDataOfLower;

            int firstHigherIndex = m_syncDataBuffer.FindIndex(_ => _.sentNetworkTime >= currentNetworkTime);
            if (firstHigherIndex >= 0)
            {
                syncDataOfHigher = m_syncDataBuffer.ElementAt(firstHigherIndex);
                syncDataOfLower = firstHigherIndex > 0 ? m_syncDataBuffer.ElementAt(firstHigherIndex - 1) : syncDataOfHigher;
            }
            else
            {
                // we are ahead of all snapshots
                syncDataOfHigher = m_syncDataBuffer.Last();
                syncDataOfLower = syncDataOfHigher;
            }

            // interpolate between lower and higher syncdata
            double ratio = (currentNetworkTime - syncDataOfLower.sentNetworkTime) / (syncDataOfHigher.sentNetworkTime - syncDataOfLower.sentNetworkTime);
            if (double.IsNaN(ratio))
                ratio = 1;
            ratio = Mathd.Clamp01(ratio);
            SyncData interpolated = DoSnapshotInterpolation(syncDataOfLower, syncDataOfHigher, ratio);
            this.Apply(interpolated);

            // remove old snapshots, but be careful not to remove the current lower snapshot
            this.RemoveOldSnapshots(System.Math.Min(currentNetworkTime, syncDataOfLower.sentNetworkTime));

        }

        private void RemoveOldSnapshots(double currentNetworkTime)
        {
            while (true)
            {
                if (m_syncDataBuffer.Count == 0)
                    break;

                SyncData syncData = m_syncDataBuffer.Peek();
                if (syncData.sentNetworkTime < currentNetworkTime)
                    m_syncDataBuffer.Dequeue();
                else
                    break;
            }
        }

        private static SyncData DoSnapshotInterpolation(SyncData from, SyncData to, double t)
        {
            return new SyncData
            {
                Position = Vector3.LerpUnclamped(from.Position, to.Position, (float)t),
                Rotation = Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, (float)t),
            };
        }

        private void UpdateClientUsingConstantVelocity()
        {
            this.SetPosition(Vector3.MoveTowards(
                this.GetPosition(),
                m_currentSyncData.Position,
                m_currentSyncData.CalculatedVelocityMagnitude * this.GetDeltaTime() * m_parameters.constantVelocityMultiplier));

            this.SetRotation(Quaternion.RotateTowards(
                this.GetRotation(),
                m_currentSyncData.Rotation,
                m_currentSyncData.CalculatedAngularVelocityMagnitude * this.GetDeltaTime() * m_parameters.constantVelocityMultiplier));

        }

        private void UpdateClientUsingLerp()
        {
            this.SetPosition(Vector3.Lerp(
                this.GetPosition(),
                m_currentSyncData.Position,
                1 - Mathf.Exp(-m_parameters.lerpFactor * this.GetDeltaTime())));

            this.SetRotation(Quaternion.Lerp(
                this.GetRotation(),
                m_currentSyncData.Rotation,
                1 - Mathf.Exp(-m_parameters.lerpFactor * this.GetDeltaTime())));

        }

        private void UpdateClientUsingSphericalLerp()
        {
            this.SetPosition(Vector3.Slerp(
                this.GetPosition(),
                m_currentSyncData.Position,
                1 - Mathf.Exp(-m_parameters.lerpFactor * this.GetDeltaTime())));

            this.SetRotation(Quaternion.Slerp(
                this.GetRotation(),
                m_currentSyncData.Rotation,
                1 - Mathf.Exp(-m_parameters.lerpFactor * this.GetDeltaTime())));

        }

        private float GetDeltaTime()
        {
            return m_parameters.useSmoothDeltaTime ? Time.smoothDeltaTime : Time.deltaTime;
        }

        public void OnValidate(Parameters parameters)
        {
            m_parameters = parameters;
        }

        public void ResetSyncDataToTransform()
        {
            if (m_hasTransform)
            {
                m_currentSyncData.Position = this.GetPosition();
                m_currentSyncData.Rotation = this.GetRotation();
            }
            m_currentSyncData.CalculatedVelocityMagnitude = float.PositiveInfinity;
            m_currentSyncData.CalculatedAngularVelocityMagnitude = float.PositiveInfinity;
            m_nextSyncData = null;
            m_syncDataBuffer.Clear();
        }

        public void WarpToLatestSyncData()
        {
            var syncData = this.GetLatestSyncData();

            // assign position/rotation directly to transform, because rigid body may not warp ?
            if (m_hasTransform)
            {
                syncData.Apply(m_transform);
            }

            m_currentSyncData = syncData;
            m_nextSyncData = null;
            m_syncDataBuffer.Clear();
        }

        public SyncData GetLatestSyncData()
        {
            return m_nextSyncData ?? m_currentSyncData;
        }

        private void Apply(SyncData syncData)
        {
            this.SetPosition(syncData.Position);
            this.SetRotation(syncData.Rotation);
        }

        private void SetPosition()
        {
            this.SetPosition(m_currentSyncData.Position);
        }

        private void SetPosition(Vector3 pos)
        {
            if (m_parameters.useRigidBody && m_hasRigidBody)
                m_rigidbody.MovePosition(pos);
            else if (m_hasTransform)
                m_transform.localPosition = pos;
        }

        private void SetRotation()
        {
            this.SetRotation(m_currentSyncData.Rotation);
        }

        private void SetRotation(Quaternion rot)
        {
            if (m_parameters.useRigidBody && m_hasRigidBody)
                m_rigidbody.MoveRotation(rot);
            else if (m_hasTransform)
                m_transform.localRotation = rot;
        }

        private Vector3 GetPosition()
        {
            if (m_parameters.useRigidBody && m_hasRigidBody)
                return m_rigidbody.position;
            if (m_hasTransform)
                return m_transform.localPosition;
            return m_currentSyncData.Position;
        }

        private Quaternion GetRotation()
        {
            if (m_parameters.useRigidBody && m_hasRigidBody)
                return m_rigidbody.rotation;
            if (m_hasTransform)
                return m_transform.localRotation;
            return m_currentSyncData.Rotation;
        }

        private bool ArrivedToCurrentSyncData()
        {
            return Vector3.Distance(m_currentSyncData.Position, this.GetPosition()) < 0.01f
                && Quaternion.Angle(m_currentSyncData.Rotation, this.GetRotation()) < 1f;
        }

        private void CheckIfArrivedToNextSyncData()
        {
            if (m_parameters.clientUpdateType == ClientUpdateType.SnapshotInterpolation)
                return;

            if (m_nextSyncData.HasValue && this.ArrivedToCurrentSyncData())
            {
                this.SetPosition();
                this.SetRotation();

                m_currentSyncData = m_nextSyncData.Value;
                m_nextSyncData = null;
            }
        }
    }
}