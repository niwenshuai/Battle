using System.Collections.Generic;
using UnityEngine;

namespace FrameSync
{
    /// <summary>
    /// 自动战斗游戏逻辑 — 实现 IGameLogic，管理角色选择和 1v1 自动对战。
    ///
    /// 流程：
    ///   1. Selecting — 玩家按 1 选剑士、2 选弓手；双方确认后进入战斗
    ///   2. Fighting  — 行为树驱动自动战斗，玩家仅可按 Space 释放大招
    ///   3. Ended     — 一方阵亡，战斗结束
    ///
    /// 通知队列 EventQueue 每帧累积战斗事件，渲染层读取后调用 ClearEvents() 清空。
    /// </summary>
    public class BattleLogic : MonoBehaviour, IGameLogic
    {
        // ── 战斗阶段 ─────────────────────────────────────────

        public enum BattlePhase : byte
        {
            Selecting, // 角色选择
            Fighting,  // 战斗中
            Ended,     // 已结束
        }

        public BattlePhase Phase { get; private set; }

        // ── 通知队列 ─────────────────────────────────────────

        /// <summary>累积的战斗事件。渲染层逐帧消费后调用 ClearEvents()。</summary>
        public readonly List<BattleEvent> EventQueue = new();

        /// <summary>渲染层消费完毕后清空事件。</summary>
        public void ClearEvents() => EventQueue.Clear();

        // ── 公开查询 ─────────────────────────────────────────

        public BattleFighter GetFighter(int playerId)
        {
            if (_fighters == null) return null;
            for (int i = 0; i < _fighters.Count; i++)
                if (_fighters[i].PlayerId == playerId) return _fighters[i];
            return null;
        }

        public List<CharacterType> GetSelections(int teamId)
        {
            if (teamId >= 1 && teamId <= 2)
                return _selections[teamId];
            return null;
        }

        public List<BattleFighter> Fighters => _fighters;

        public byte LocalPlayerId => _localPlayerId;

        // ── 内部状态 ─────────────────────────────────────────

        byte _localPlayerId;
        int _localSelectionSlot; // 当前正在选第几个角色 (0..TeamSize-1)
        readonly List<CharacterType>[] _selections = { null, new(), new() }; // 1-based teams
        List<BattleFighter> _fighters;
        readonly List<Projectile> _projectiles = new();
        readonly List<AoEProjectile> _aoeProjectiles = new();
        readonly List<PiercingProjectile> _piercingProjectiles = new();
        readonly List<LightningCloud> _lightningClouds = new();
        byte _nextFighterId;
        int _teamSize;

        static readonly FixedInt TickInterval = FixedInt.OneVal / FixedInt.FromInt(15);

        // ═══════════════════════════════════════════════════════
        //  IGameLogic 实现
        // ═══════════════════════════════════════════════════════

        public void OnGameStart(int playerCount, byte localPlayerId, int randomSeed)
        {
            _localPlayerId = localPlayerId;
            _localSelectionSlot = 0;
            _selections[1].Clear();
            _selections[2].Clear();
            _fighters = null;
            _teamSize = CharacterConfig.TeamSize;
            Phase = BattlePhase.Selecting;
            EventQueue.Clear();

            EventQueue.Add(new BattleEvent
            {
                Type     = BattleEventType.PhaseChanged,
                IntParam = (int)BattlePhase.Selecting,
            });

            Debug.Log($"[Battle] 游戏开始 — 每队选{_teamSize}个角色 1=剑士 2=弓手 3=刺客 4=法师  localId={localPlayerId}");
        }

        public void OnLogicUpdate(FrameData frame)
        {
            switch (Phase)
            {
                case BattlePhase.Selecting: TickSelection(frame); break;
                case BattlePhase.Fighting:  TickCombat(frame);    break;
            }
        }

        public void OnGameEnd(byte winnerId)
        {
            Debug.Log($"[Battle] 服务器结束游戏 winner={winnerId}");
        }

        public PlayerInput SampleLocalInput()
        {
            int mx = 0;
            uint buttons = 0;

            if (Phase == BattlePhase.Selecting)
            {
                // 多角色选择：每次按键添加一个角色到队伍
                CharacterType pick = CharacterType.None;
                if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
                    pick = CharacterType.Warrior;
                if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
                    pick = CharacterType.Archer;
                if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
                    pick = CharacterType.Assassin;
                if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
                    pick = CharacterType.Mage;
                if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5))
                    pick = CharacterType.Healer;
                if (Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6))
                    pick = CharacterType.Witch;
                if (Input.GetKeyDown(KeyCode.Alpha7) || Input.GetKeyDown(KeyCode.Keypad7))
                    pick = CharacterType.Barbarian;
                if (Input.GetKeyDown(KeyCode.Alpha8) || Input.GetKeyDown(KeyCode.Keypad8))
                    pick = CharacterType.LightningMage;
                if (pick != CharacterType.None)
                    mx = (int)pick;
            }
            else if (Phase == BattlePhase.Fighting)
            {
                if (Input.GetKey(KeyCode.Space))
                    buttons |= PlayerInput.ButtonFire;
            }

            return new PlayerInput { MoveX = mx, Buttons = buttons };
        }

        // ═══════════════════════════════════════════════════════
        //  选角阶段
        // ═══════════════════════════════════════════════════════

        void TickSelection(FrameData frame)
        {
            foreach (var input in frame.Inputs)
            {
                byte pid = input.PlayerId;
                if (pid < 1 || pid > 2) continue;
                if (_selections[pid].Count >= _teamSize) continue;

                if (input.MoveX >= (int)CharacterType.Warrior &&
                    input.MoveX <= (int)CharacterType.LightningMage)
                {
                    var charType = (CharacterType)input.MoveX;
                    _selections[pid].Add(charType);

                    EventQueue.Add(new BattleEvent
                    {
                        Frame    = frame.FrameId,
                        Type     = BattleEventType.CharSelected,
                        SourceId = pid,
                        IntParam = input.MoveX,
                    });

                    Debug.Log($"[Battle] 玩家{pid} 选择 {charType} ({_selections[pid].Count}/{_teamSize})");
                }
            }

            // 双方都选满 → 开始战斗
            if (_selections[1].Count >= _teamSize && _selections[2].Count >= _teamSize)
                BeginCombat(frame.FrameId);
        }

        void BeginCombat(int frame)
        {
            Phase = BattlePhase.Fighting;

            _fighters = new List<BattleFighter>();
            var formation = CharacterConfig.Formation;
            byte nextId = 1;

            // 创建队伍1角色
            for (int i = 0; i < _teamSize; i++)
            {
                var f = new BattleFighter();
                var pos = i < formation.Length
                    ? new FixedVector2(FixedInt.FromInt(formation[i].X), FixedInt.FromInt(formation[i].Y))
                    : new FixedVector2(FixedInt.FromInt(-5), FixedInt.FromInt(-2 + i * 2));
                f.Init(_selections[1][i], nextId++, 1, pos);
                _fighters.Add(f);
            }

            // 创建队伍2角色（X镜像）
            for (int i = 0; i < _teamSize; i++)
            {
                var f = new BattleFighter();
                var pos = i < formation.Length
                    ? new FixedVector2(FixedInt.FromInt(-formation[i].X), FixedInt.FromInt(formation[i].Y))
                    : new FixedVector2(FixedInt.FromInt(5), FixedInt.FromInt(-2 + i * 2));
                f.Init(_selections[2][i], nextId++, 2, pos);
                _fighters.Add(f);
            }

            // 构建行为树（所有角色互相引用）
            for (int i = 0; i < _fighters.Count; i++)
                _fighters[i].BuildBT(_fighters, EventQueue);

            // 通知显示层角色创建
            for (int i = 0; i < _fighters.Count; i++)
            {
                var fi = _fighters[i];
                EventQueue.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.FighterSpawn,
                    SourceId = fi.PlayerId,
                    TargetId = (byte)((fi.TeamId << 4) | (int)fi.CharType),
                    IntParam = fi.MaxHp.ToInt(),
                    PosXRaw  = fi.Position.X.Raw,
                    PosYRaw  = fi.Position.Y.Raw,
                });
            }

            EventQueue.Add(new BattleEvent
            {
                Frame = frame,
                Type  = BattleEventType.BattleStart,
            });

            _nextFighterId = nextId;

            EventQueue.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.PhaseChanged,
                IntParam = (int)BattlePhase.Fighting,
            });

            Debug.Log($"[Battle] ===== 战斗开始！ {_teamSize}v{_teamSize} =====");
        }

        // ═══════════════════════════════════════════════════════
        //  战斗阶段
        // ═══════════════════════════════════════════════════════

        void TickCombat(FrameData frame)
        {
            // 1. 读取玩家大招指令 — 对该玩家队伍所有活着的角色生效
            foreach (var input in frame.Inputs)
            {
                byte pid = input.PlayerId;
                if (pid < 1 || pid > 2) continue;
                if ((input.Buttons & PlayerInput.ButtonFire) == 0) continue;

                for (int i = 0; i < _fighters.Count; i++)
                {
                    var f = _fighters[i];
                    if (f.TeamId == pid && !f.IsDead)
                        f.UltRequested = true;
                }
            }

            // 2. 驱动所有角色行为树
            for (int i = 0; i < _fighters.Count; i++)
                _fighters[i].Tick(frame.FrameId, TickInterval);

            // 2.5 收集新弹射物
            for (int i = 0; i < _fighters.Count; i++)
            {
                if (_fighters[i].PendingProjectiles.Count > 0)
                {
                    _projectiles.AddRange(_fighters[i].PendingProjectiles);
                    _fighters[i].PendingProjectiles.Clear();
                }
                if (_fighters[i].PendingAoEProjectiles.Count > 0)
                {
                    _aoeProjectiles.AddRange(_fighters[i].PendingAoEProjectiles);
                    _fighters[i].PendingAoEProjectiles.Clear();
                }
                if (_fighters[i].PendingPiercingProjectiles.Count > 0)
                {
                    _piercingProjectiles.AddRange(_fighters[i].PendingPiercingProjectiles);
                    _fighters[i].PendingPiercingProjectiles.Clear();
                }
            }

            // 2.5b 处理召唤请求
            for (int i = 0; i < _fighters.Count; i++)
            {
                if (_fighters[i].PendingSummons.Count > 0)
                {
                    foreach (var req in _fighters[i].PendingSummons)
                        CreateSummon(_fighters[i], req, frame.FrameId);
                    _fighters[i].PendingSummons.Clear();
                }
                if (_fighters[i].PendingLightningClouds.Count > 0)
                {
                    foreach (var req in _fighters[i].PendingLightningClouds)
                    {
                        var cloud = new LightningCloud();
                        cloud.Init(req, _fighters, _fighters[i]);
                        _lightningClouds.Add(cloud);

                        // 显示层事件：闪电云生成
                        EventQueue.Add(new BattleEvent
                        {
                            Frame    = frame.FrameId,
                            Type     = BattleEventType.LightningCloudSpawn,
                            SourceId = req.SourceId,
                            PosXRaw  = req.Position.X.Raw,
                            PosYRaw  = req.Position.Y.Raw,
                            IntParam = req.Lifetime, // 存活帧数，用于显示层计时
                        });
                    }
                    _fighters[i].PendingLightningClouds.Clear();
                }
            }

            // 2.6 驱动弹射物飞行
            for (int p = _projectiles.Count - 1; p >= 0; p--)
            {
                if (_projectiles[p].Tick(TickInterval, frame.FrameId, EventQueue))
                    _projectiles.RemoveAt(p);
            }

            // 2.6b 驱动AoE弹射物飞行
            for (int p = _aoeProjectiles.Count - 1; p >= 0; p--)
            {
                if (_aoeProjectiles[p].Tick(TickInterval, frame.FrameId, EventQueue))
                    _aoeProjectiles.RemoveAt(p);
            }

            // 2.6c 驱动穿刺弹射物飞行
            for (int p = _piercingProjectiles.Count - 1; p >= 0; p--)
            {
                if (_piercingProjectiles[p].Tick(TickInterval, frame.FrameId, EventQueue))
                    _piercingProjectiles.RemoveAt(p);
            }

            // 2.6d 驱动闪电云
            for (int p = _lightningClouds.Count - 1; p >= 0; p--)
            {
                if (_lightningClouds[p].Tick(frame.FrameId, EventQueue))
                    _lightningClouds.RemoveAt(p);
            }

            // 2.7 碰撞分离
            BattleFighter.ResolveCollisions(_fighters);

            // 2.8 召唤物生命周期
            for (int i = _fighters.Count - 1; i >= 0; i--)
            {
                var f = _fighters[i];
                if (!f.IsSummon || f.IsDead) continue;
                // 主人死亡 → 召唤物消失
                if (f.Master.IsDead)
                {
                    f.Hp = FixedInt.Zero;
                    EventQueue.Add(new BattleEvent { Frame = frame.FrameId, Type = BattleEventType.Death, SourceId = f.PlayerId });
                    continue;
                }
                // 存活时间到期
                if (f.LifetimeLeft > 0)
                {
                    f.LifetimeLeft--;
                    if (f.LifetimeLeft <= 0)
                    {
                        f.Hp = FixedInt.Zero;
                        EventQueue.Add(new BattleEvent { Frame = frame.FrameId, Type = BattleEventType.Death, SourceId = f.PlayerId });
                    }
                }
            }

            // 3. 胜负判定：一方所有非召唤物角色阵亡
            bool team1Dead = true, team2Dead = true;
            for (int i = 0; i < _fighters.Count; i++)
            {
                if (_fighters[i].IsSummon) continue; // 召唤物不计入胜负
                if (_fighters[i].TeamId == 1 && !_fighters[i].IsDead) team1Dead = false;
                if (_fighters[i].TeamId == 2 && !_fighters[i].IsDead) team2Dead = false;
            }

            if (team1Dead || team2Dead)
            {
                byte winnerId = team1Dead ? (byte)2 : (byte)1;
                Phase = BattlePhase.Ended;

                EventQueue.Add(new BattleEvent
                {
                    Frame    = frame.FrameId,
                    Type     = BattleEventType.BattleEnd,
                    IntParam = winnerId,
                });

                EventQueue.Add(new BattleEvent
                {
                    Frame    = frame.FrameId,
                    Type     = BattleEventType.PhaseChanged,
                    IntParam = (int)BattlePhase.Ended,
                });

                Debug.Log($"[Battle] ===== 队伍{winnerId} 获胜！ =====");
            }
        }

        void CreateSummon(BattleFighter master, SummonRequest req, int frame)
        {
            var arenaHalf = FixedInt.FromInt(CharacterConfig.ArenaHalf);
            var pos = new FixedVector2(
                FixedInt.Clamp(req.Position.X, -arenaHalf, arenaHalf),
                FixedInt.Clamp(req.Position.Y, -arenaHalf, arenaHalf));

            var f = new BattleFighter();
            f.Init(req.Type, _nextFighterId++, master.TeamId, pos);
            f.Master = master;
            f.LifetimeLeft = req.Lifetime;

            if (req.MaxHp > 0)
            {
                f.MaxHp = FixedInt.FromInt(req.MaxHp);
                f.Hp = f.MaxHp;
            }

            _fighters.Add(f);
            f.BuildBT(_fighters, EventQueue);

            EventQueue.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.FighterSpawn,
                SourceId = f.PlayerId,
                TargetId = (byte)((f.TeamId << 4) | (int)f.CharType),
                IntParam = f.MaxHp.ToInt(),
                PosXRaw  = f.Position.X.Raw,
                PosYRaw  = f.Position.Y.Raw,
            });

            Debug.Log($"[Battle] 召唤物开始 #{f.PlayerId} {req.Type} HP={req.MaxHp} 存活{req.Lifetime}帧");
        }
    }
}
