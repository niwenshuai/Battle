using System.Collections.Generic;
using UnityEngine;

namespace FrameSync
{
    /// <summary>
    /// 运行时 NavMesh 代理组件。
    ///
    /// 用法：
    /// 1. 挂载到 GameObject 上
    /// 2. 赋值 NavMeshAsset
    /// 3. 调用 SetDestination(target) 开始寻路
    /// 4. 在帧同步 OnLogicUpdate 中调用 Tick() 驱动移动
    ///
    /// 所有寻路和移动逻辑使用定点数。
    /// Mono 部分仅负责从定点数位置同步到 Transform（渲染用）。
    /// </summary>
    [ExecuteAlways]
    public class FixedNavMeshAgent : MonoBehaviour
    {
        [Header("NavMesh")]
        public FixedNavMeshAsset NavMeshAsset;

        [Header("Movement")]
        public float Speed = 5f;
        public float StoppingDistance = 0.1f;

        // ── 定点数状态（帧同步安全）────────────────────────
        private FixedNavMeshData _navData;
        private FixedNavMeshPathfinder _pathfinder;

        private FixedVector2 _position;
        private FixedVector2 _destination;
        private FixedInt _speed;
        private FixedInt _stoppingDist;

        private List<FixedVector2> _path;
        private int _currentPathIndex;
        private bool _hasPath;

        // ── 公开 API ─────────────────────────────────────────

        /// <summary>当前定点数位置。</summary>
        public FixedVector2 Position => _position;
        public bool HasPath => _hasPath;
        public bool ReachedDestination => !_hasPath;

        /// <summary>初始化（在帧同步 OnGameStart 中调用）。</summary>
        public void Init(FixedVector2 startPos)
        {
            if (NavMeshAsset == null)
            {
                Debug.LogError("[FixedNavMeshAgent] NavMeshAsset is null!");
                return;
            }

            _navData = NavMeshAsset.ToRuntimeData();
            _pathfinder = new FixedNavMeshPathfinder(_navData);
            _position = startPos;
            _speed = FixedInt.FromFloat(Speed);
            _stoppingDist = FixedInt.FromFloat(StoppingDistance);
            _hasPath = false;
            _path = null;

            SyncTransform();
        }

        /// <summary>设置目标点，自动寻路。</summary>
        public bool SetDestination(FixedVector2 target)
        {
            if (_pathfinder == null) return false;

            _destination = target;
            _path = _pathfinder.FindPath(_position, target);

            if (_path == null || _path.Count < 2)
            {
                _hasPath = false;
                return false;
            }

            _currentPathIndex = 1; // 跳过起点
            _hasPath = true;
            return true;
        }

        /// <summary>
        /// 帧同步逻辑帧驱动。每逻辑帧调用一次。
        /// </summary>
        /// <param name="dt">逻辑帧时间步长（定点数）。</param>
        public void Tick(FixedInt dt)
        {
            if (!_hasPath || _path == null) return;

            var moveAmount = _speed * dt;

            while (moveAmount > FixedInt.Zero && _currentPathIndex < _path.Count)
            {
                var target = _path[_currentPathIndex];
                var toTarget = target - _position;
                var dist = toTarget.Magnitude;

                if (dist <= moveAmount)
                {
                    // 到达当前路径点
                    _position = target;
                    moveAmount = moveAmount - dist;
                    _currentPathIndex++;
                }
                else
                {
                    // 朝路径点移动
                    _position = _position + toTarget / dist * moveAmount;
                    moveAmount = FixedInt.Zero;
                }
            }

            // 检查是否到达终点
            if (_currentPathIndex >= _path.Count)
            {
                _hasPath = false;
            }
            else
            {
                // 检查停止距离
                var distToDest = FixedVector2.Distance(_position, _destination);
                if (distToDest <= _stoppingDist)
                    _hasPath = false;
            }
        }

        /// <summary>将定点数位置同步到 Transform（XZ 平面）。仅用于渲染。</summary>
        public void SyncTransform()
        {
            var pos = transform.position;
            pos.x = _position.X.ToFloat();
            pos.z = _position.Y.ToFloat();
            transform.position = pos;
        }

        /// <summary>停止移动。</summary>
        public void Stop()
        {
            _hasPath = false;
            _path = null;
        }

        // ── Gizmos ───────────────────────────────────────────

        #if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (_path == null || _path.Count < 2) return;

            Gizmos.color = Color.green;
            for (int i = 0; i < _path.Count - 1; i++)
            {
                var a = new Vector3(_path[i].X.ToFloat(), 0.1f, _path[i].Y.ToFloat());
                var b = new Vector3(_path[i + 1].X.ToFloat(), 0.1f, _path[i + 1].Y.ToFloat());
                Gizmos.DrawLine(a, b);
            }

            Gizmos.color = Color.red;
            Gizmos.DrawSphere(new Vector3(_destination.X.ToFloat(), 0.1f, _destination.Y.ToFloat()), 0.15f);
        }
        #endif
    }
}
