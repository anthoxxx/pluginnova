using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Life;
using Life.CharacterSystem;
using Life.Network;
using Mirror;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PNJCreator
{
    /// <summary>Un PNJ présent dans le monde.</summary>
    public class NpcInstance
    {
        public NpcData Data;
        public GameObject Go;
        public CharacterSetup Setup;

        // Patrouille
        public int WaypointIndex;
        public int Direction = 1;
        public float WaitUntil;
        public bool Moving;

        // Animation (NetworkAnimator.animator, via réflexion)
        public object Animator;
        public MethodInfo SetFloat;
        public MethodInfo SetBool;

        public bool Alive => Go != null;
        public Vector3 Position => Go != null ? Go.transform.position : Data.Position;
    }

    /// <summary>
    /// Crée les PNJ à partir du prefab joueur du jeu (LifeNetworkManager.malePrefab / femalePrefab),
    /// sans connexion ni objet Player : ce ne sont pas de faux joueurs.
    /// </summary>
    public class NpcManager
    {
        readonly DataStore store;
        public readonly Dictionary<int, NpcInstance> Instances = new Dictionary<int, NpcInstance>();

        static readonly string[] AnimSpeedParams = { "ForwardSpeed", "HorizontalSpeed" };

        // Composants de déplacement/entrée qui n'ont pas de sens sur un PNJ piloté par le serveur.
        static readonly HashSet<string> ServerDisabledBehaviours = new HashSet<string>
        {
            "AnimationControl", "AdvancedWalkerController", "SimpleWalkerController", "Mover",
            "CharacterKeyboardInput", "CharacterJoystickInput", "TurnTowardControllerVelocity",
        };

        public NpcManager(DataStore store) { this.store = store; }

        public static bool ServerReady =>
            NetworkServer.active && NetworkManager.singleton is LifeNetworkManager nm && nm.malePrefab != null && nm.femalePrefab != null;

        // ------------------------------------------------------------------
        //  Apparition / disparition
        // ------------------------------------------------------------------

        public void SpawnAll()
        {
            foreach (var data in store.Config.Npcs.ToList()) Spawn(data);
            Debug.Log($"[PNJCreator] {Instances.Count} PNJ chargé(s).");
        }

        public void DespawnAll()
        {
            foreach (int id in Instances.Keys.ToList()) Despawn(id);
        }

        public NpcInstance Spawn(NpcData data)
        {
            Despawn(data.Id);
            if (!ServerReady) return null;
            try
            {
                var nm = (LifeNetworkManager)NetworkManager.singleton;
                GameObject prefab = data.Sex == 1 ? nm.femalePrefab : nm.malePrefab;
                GameObject go = Object.Instantiate(prefab, data.Position, Quaternion.Euler(0f, data.RotationY, 0f));
                go.name = "PNJCreator_" + data.Id;
                // L'échelle fait partie du message de spawn Mirror : elle est donc vue par tous les clients.
                go.transform.localScale = Vector3.one * data.Scale;

                var setup = go.GetComponent<CharacterSetup>();
                if (setup == null)
                {
                    Object.Destroy(go);
                    Debug.LogError("[PNJCreator] Le prefab joueur n'a pas de CharacterSetup : version du jeu incompatible ?");
                    return null;
                }

                CharacterCustomizationSetup skin = SkinOf(data);
                if (skin != null) SetSyncVar(setup, "NetworkcharacterSkinData", skin);
                setup.NetworkFullName = data.Name;
                SetSyncVar(setup, "NetworkHealth", 100);
                SetSyncVar(setup, "NetworkHunger", 100);
                SetSyncVar(setup, "NetworkThirst", 100);
                SetSyncVar(setup, "NetworkusernameColor", Nova.HexToColor(store.Config.General.NameColor));

                var inst = new NpcInstance { Data = data, Go = go, Setup = setup };
                ForceServerAuthority(go);
                NetworkServer.Spawn(go);
                Neutralize(inst);
                Instances[data.Id] = inst;
                return inst;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PNJCreator] Impossible de faire apparaître le PNJ {data.Id} ({data.Name}) : {e}");
                return null;
            }
        }

        public void Despawn(int id)
        {
            if (!Instances.TryGetValue(id, out var inst)) return;
            Instances.Remove(id);
            if (inst.Go == null) return;
            try
            {
                if (NetworkServer.active) NetworkServer.Destroy(inst.Go);
                else Object.Destroy(inst.Go);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PNJCreator] Erreur à la suppression du PNJ {id} : {e.Message}");
            }
        }

        public NpcInstance Respawn(NpcData data) => Spawn(data);

        /// <summary>
        /// Le prefab joueur synchronise sa position depuis le client propriétaire. Un PNJ n'a pas de
        /// propriétaire : on bascule NetworkTransform / NetworkAnimator en mode serveur → clients.
        /// Réflexion pour rester compatible avec les différentes versions de Mirror.
        /// </summary>
        static void ForceServerAuthority(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                Type t = comp.GetType();
                if (!(t.Name.Contains("NetworkTransform") || t.Name == "NetworkAnimator")) continue;
                FieldInfo dir = FindField(t, "syncDirection");
                if (dir != null && dir.FieldType.IsEnum && Enum.IsDefined(dir.FieldType, "ServerToClient"))
                    dir.SetValue(comp, Enum.Parse(dir.FieldType, "ServerToClient"));
                FieldInfo clientAuth = FindField(t, "clientAuthority");
                if (clientAuth != null && clientAuth.FieldType == typeof(bool)) clientAuth.SetValue(comp, false);
            }
        }

        /// <summary>Coupe tout ce qui suppose un vrai joueur derrière le personnage.</summary>
        void Neutralize(NpcInstance inst)
        {
            // OnStartServer lance SecondUpdate/MinuteUpdate qui utilisent setup.player (null ici).
            inst.Setup.CancelInvoke();

            foreach (var b in inst.Go.GetComponentsInChildren<Behaviour>(true))
                if (b != null && ServerDisabledBehaviours.Contains(b.GetType().Name)) b.enabled = false;

            Component rb = inst.Go.GetComponent("Rigidbody");
            if (rb != null)
            {
                SetMember(rb, "isKinematic", true);
                SetMember(rb, "useGravity", false);
            }

            foreach (var comp in inst.Go.GetComponents<Component>())
            {
                if (comp == null || comp.GetType().Name != "NetworkAnimator") continue;
                object animator = GetMember(comp, "animator");
                if (animator == null) break;
                inst.Animator = animator;
                inst.SetFloat = animator.GetType().GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                inst.SetBool = animator.GetType().GetMethod("SetBool", new[] { typeof(string), typeof(bool) });
                break;
            }
            SetAnimSpeed(inst, 0f);
        }

        // ------------------------------------------------------------------
        //  Apparence
        // ------------------------------------------------------------------

        public static CharacterCustomizationSetup SkinOf(NpcData data)
        {
            if (data.Skin == null) return null;
            try { return CharacterCustomizationSetup.DeserializeFromJson(data.Skin.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[PNJCreator] Apparence illisible pour le PNJ {data.Id} : {e.Message}");
                return null;
            }
        }

        public static void StoreSkin(NpcData data, CharacterCustomizationSetup skin)
        {
            data.Skin = skin == null ? null : JObject.Parse(skin.SerializeToJson());
        }

        public static CharacterCustomizationSetup CloneSkin(CharacterCustomizationSetup skin) =>
            skin == null ? null : CharacterCustomizationSetup.DeserializeFromJson(skin.SerializeToJson());

        /// <summary>Modifie l'apparence, la sauvegarde et l'applique en direct à tous les clients.</summary>
        public void EditSkin(NpcData data, Action<CharacterCustomizationSetup> edit)
        {
            CharacterCustomizationSetup skin = SkinOf(data) ?? new CharacterCustomizationSetup();
            edit(skin);
            StoreSkin(data, skin);
            store.SaveConfig();
            ApplySkin(data);
        }

        public void ApplySkin(NpcData data)
        {
            if (!Instances.TryGetValue(data.Id, out var inst) || !inst.Alive) return;
            CharacterCustomizationSetup skin = SkinOf(data);
            if (skin == null) return;
            // Nouvel objet → la SyncVar est marquée modifiée (joueurs qui arrivent plus tard)…
            SetSyncVar(inst.Setup, "NetworkcharacterSkinData", skin);
            // …et le RPC du jeu applique le changement immédiatement chez les joueurs présents.
            inst.Setup.RpcSkinChange(skin);
        }

        public void ApplyName(NpcData data)
        {
            if (Instances.TryGetValue(data.Id, out var inst) && inst.Alive) inst.Setup.NetworkFullName = data.Name;
        }

        // ------------------------------------------------------------------
        //  Déplacement
        // ------------------------------------------------------------------

        /// <summary>Téléportation : on refait apparaître le PNJ, la position du spawn étant fiable pour tous les clients.</summary>
        public void MoveTo(NpcData data, Vector3 position, float rotationY)
        {
            data.Position = position;
            data.RotationY = Mathf.Repeat(rotationY, 360f);
            store.SaveConfig();
            Respawn(data);
        }

        /// <summary>Appelé à chaque frame par NpcRunner.</summary>
        public void Tick(float dt, float now)
        {
            foreach (var inst in Instances.Values)
            {
                if (!inst.Alive) continue;
                var d = inst.Data;
                if (!d.Mobile || d.Waypoints.Count == 0)
                {
                    if (inst.Moving) { inst.Moving = false; SetAnimSpeed(inst, 0f); }
                    continue;
                }
                if (now < inst.WaitUntil) continue;

                if (inst.WaypointIndex < 0 || inst.WaypointIndex >= d.Waypoints.Count) inst.WaypointIndex = 0;
                Transform tr = inst.Go.transform;
                Vector3 target = d.Waypoints[inst.WaypointIndex].Position;
                Vector3 pos = tr.position;
                Vector3 flat = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
                float dist = flat.magnitude;

                if (dist < 0.15f)
                {
                    tr.position = target;
                    inst.Moving = false;
                    SetAnimSpeed(inst, 0f);
                    inst.WaitUntil = now + d.WaitSeconds;
                    AdvanceWaypoint(inst);
                    continue;
                }

                float step = Mathf.Min(d.Speed * dt, dist);
                // Hauteur interpolée entre la position actuelle et le point visé (points posés au sol).
                float t = step / dist;
                tr.position = new Vector3(pos.x + flat.x / dist * step, Mathf.Lerp(pos.y, target.y, t), pos.z + flat.z / dist * step);
                Quaternion look = Quaternion.LookRotation(flat / dist, Vector3.up);
                tr.rotation = Quaternion.RotateTowards(tr.rotation, look, 360f * dt);

                if (!inst.Moving) { inst.Moving = true; SetAnimSpeed(inst, d.Speed); }
            }
        }

        static void AdvanceWaypoint(NpcInstance inst)
        {
            int count = inst.Data.Waypoints.Count;
            if (count <= 1) { inst.WaypointIndex = 0; return; }
            if (inst.Data.LoopPatrol)
            {
                inst.WaypointIndex = (inst.WaypointIndex + 1) % count;
                return;
            }
            int next = inst.WaypointIndex + inst.Direction;
            if (next < 0 || next >= count) { inst.Direction = -inst.Direction; next = inst.WaypointIndex + inst.Direction; }
            inst.WaypointIndex = next;
        }

        public void ResetPatrol(NpcData data)
        {
            if (!Instances.TryGetValue(data.Id, out var inst)) return;
            inst.WaypointIndex = 0;
            inst.Direction = 1;
            inst.WaitUntil = 0f;
        }

        static void SetAnimSpeed(NpcInstance inst, float speed)
        {
            if (inst.Animator == null) return;
            try
            {
                if (inst.SetFloat != null)
                    foreach (string p in AnimSpeedParams) inst.SetFloat.Invoke(inst.Animator, new object[] { p, speed });
                inst.SetBool?.Invoke(inst.Animator, new object[] { "IsGrounded", true });
            }
            catch { /* paramètre absent de l'animator : sans gravité */ }
        }

        // ------------------------------------------------------------------
        //  Outils
        // ------------------------------------------------------------------

        public NpcInstance Closest(Vector3 from, float maxDistance, Func<NpcInstance, bool> filter = null)
        {
            NpcInstance best = null;
            float bestDist = maxDistance;
            foreach (var inst in Instances.Values)
            {
                if (!inst.Alive || (filter != null && !filter(inst))) continue;
                float dist = Vector3.Distance(from, inst.Position);
                if (dist <= bestDist) { best = inst; bestDist = dist; }
            }
            return best;
        }

        public string DebugInfo(NpcInstance inst)
        {
            var sb = new StringBuilder();
            var d = inst.Data;
            sb.AppendLine($"PNJ #{d.Id} « {d.Name} » ({d.SexLabel}, échelle {d.Scale:0.##})");
            sb.AppendLine($"netId {inst.Setup.netId}, position {inst.Position}, rotation {inst.Go.transform.eulerAngles.y:0}°");
            sb.AppendLine(d.Mobile
                ? $"Mobile : {d.Waypoints.Count} point(s), cible {inst.WaypointIndex}, vitesse {d.Speed:0.##} m/s, {(inst.Moving ? "en marche" : "à l'arrêt")}"
                : "Statique");
            sb.AppendLine($"IA : {(d.AiEnabled ? "activée" : "désactivée")}, discussion {d.TalkRange:0.#} m, écoute {d.HearRange:0.#} m");
            sb.AppendLine($"Animator : {(inst.Animator != null ? "trouvé" : "absent")}");
            foreach (var comp in inst.Go.GetComponents<Component>())
            {
                if (comp == null) continue;
                Type t = comp.GetType();
                if (!(t.Name.Contains("NetworkTransform") || t.Name == "NetworkAnimator")) continue;
                object dir = FindField(t, "syncDirection")?.GetValue(comp);
                object auth = FindField(t, "clientAuthority")?.GetValue(comp);
                sb.AppendLine($"{t.Name} : syncDirection={dir ?? "-"} clientAuthority={auth ?? "-"}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// L'obfuscation du jeu laisse deux propriétés du même nom pour certaines SyncVar
        /// (une en lecture seule, une avec setter) : C# ne peut pas choisir, on passe par la réflexion.
        /// </summary>
        public static void SetSyncVar(CharacterSetup setup, string property, object value)
        {
            foreach (var p in typeof(CharacterSetup).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.Name != property || !p.CanWrite) continue;
                try { p.SetValue(setup, value, null); }
                catch (Exception e) { Debug.LogWarning($"[PNJCreator] {property} : {(e.InnerException ?? e).Message}"); }
                return;
            }
            Debug.LogWarning($"[PNJCreator] SyncVar {property} introuvable (version du jeu différente ?).");
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        static object GetMember(object target, string name)
        {
            Type t = target.GetType();
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanRead) return p.GetValue(target, null);
            return FindField(t, name)?.GetValue(target);
        }

        static void SetMember(object target, string name, object value)
        {
            try
            {
                Type t = target.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
                FindField(t, name)?.SetValue(target, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PNJCreator] {target.GetType().Name}.{name} : {e.Message}");
            }
        }
    }
}
