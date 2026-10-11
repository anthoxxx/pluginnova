// ════════════════════════════════════════════════════════════════════════════════
//  NovaPnj — PNJ réels pour Nova Life (Amboise)                 by matheo mercier
//
//  • Plugin autonome (Life.Plugin) : AUCUNE dépendance à ModKit ni SQLite.
//    Références nécessaires : Assembly-CSharp, Mirror, Newtonsoft.Json,
//    UnityEngine.CoreModule (exactement comme PNJCreator).
//  • Les PNJ sont de vrais "joueurs" spawnés côté serveur avec malePrefab / femalePrefab
//    (LifeNetworkManager), donc visibles par tous les clients.
//  • Sauvegarde automatique dans  <dossier plugins>/NovaPnj/pnjs.json  → ils reviennent
//    à chaque redémarrage du serveur, au même endroit, avec la même apparence.
//
//  Commandes (modifiables dans Cfg) :
//    /novapnj  (/pnjadmin)   menu admin : créer / modifier / supprimer les PNJ
//    /pnj                    parler au PNJ le plus proche
//    /novapnjreload          recharge le fichier JSON et respawn tous les PNJ (admin)
//    /novapnjdebug           infos sur le PNJ le plus proche (admin)
// ════════════════════════════════════════════════════════════════════════════════
using Life;
using Life.CharacterSystem;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NovaPnj
{
    // ───────────────────────────── CONFIGURATION (tout est ici) ─────────────────────────────
    public static class Cfg
    {
        public const string PluginTitle = "PNJ by matheo mercier";   // titre des notifications et menus
        public const string Credit = "matheo mercier";
        public const string Version = "2.0.0";

        public const string FolderName = "NovaPnj";                   // sous-dossier du dossier plugins
        public const string DataFileName = "pnjs.json";               // fichier de sauvegarde

        public const float SpawnDelaySeconds = 5f;                    // délai après que le serveur soit prêt avant de spawn les PNJ
        public const float RespawnCheckSeconds = 10f;                 // vérifie régulièrement que les PNJ existent encore
        public const int MaxSpawnFailures = 5;                        // abandon (jusqu'au /novapnjreload) après N échecs de spawn
        public const string NameColorHex = "#f7af57";                 // couleur du nom au-dessus de la tête

        public static readonly bool RequireAdmin = true;              // menu réservé aux admins
        public const int RequiredAdminLevel = 1;                      // niveau admin minimum
        public static readonly bool RequireAdminService = true;       // il faut être en service admin

        public const float DefaultInteractRange = 3f;                 // portée de la commande /pnj (mètres)
        public const float DefaultScale = 1f;

        public const string CmdAdmin = "/novapnj";
        public static readonly string[] CmdAdminAliases = { "/pnjadmin" };
        public const string CmdTalk = "/pnj";
        public const string CmdReload = "/novapnjreload";
        public const string CmdDebug = "/novapnjdebug";

        public const int MaxListed = 300;                             // max de vêtements affichés dans une liste
    }

    // ───────────────────────────── DONNÉES ─────────────────────────────
    public class NpcData
    {
        public int Id;
        public string Name = "PNJ";
        public string Title = "";
        public int Sex = 1;                                           // 1 = homme (malePrefab), 0 = femme (femalePrefab) — attention : dans le jeu, SexId 0 = homme
        public float Scale = Cfg.DefaultScale;
        public float X, Y, Z, RotationY;
        public bool Enabled = true;
        public float InteractRange = Cfg.DefaultInteractRange;
        public List<string> Lines = new List<string>();
        public string Skin = "";                                      // CharacterCustomizationSetup sérialisé par le jeu

        [JsonIgnore]
        public Vector3 Position
        {
            get { return new Vector3(X, Y, Z); }
            set { X = value.x; Y = value.y; Z = value.z; }
        }

        [JsonIgnore]
        public string SexLabel { get { return Sex == 1 ? "Homme" : "Femme"; } }
    }

    public class SaveFile
    {
        public int FileVersion = 1;
        public List<NpcData> Npcs = new List<NpcData>();
    }

    public class DataStore
    {
        public readonly string Folder;
        public readonly string FilePath;
        public SaveFile Data = new SaveFile();

        public DataStore(string pluginsPath)
        {
            Folder = Path.Combine(pluginsPath, Cfg.FolderName);
            FilePath = Path.Combine(Folder, Cfg.DataFileName);
        }

        public void Load()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (!File.Exists(FilePath)) { Data = new SaveFile(); Save(); return; }
                var json = File.ReadAllText(FilePath);
                var loaded = JsonConvert.DeserializeObject<SaveFile>(json);
                Data = loaded ?? new SaveFile();
                if (Data.Npcs == null) Data.Npcs = new List<NpcData>();
            }
            catch (Exception e)
            {
                try
                {
                    var backup = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(FilePath, backup, true);
                    Debug.LogWarning("[NovaPnj] " + FilePath + " illisible (" + e.Message + "). Copie : " + backup + ". Valeurs par défaut utilisées.");
                }
                catch (Exception) { }
                Data = new SaveFile();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(Data, Formatting.Indented));
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
            catch (Exception e) { Debug.LogError("[NovaPnj] Impossible d'écrire " + FilePath + " : " + e.Message); }
        }

        public int NextId()
        {
            return Data.Npcs.Count == 0 ? 1 : Data.Npcs.Max(n => n.Id) + 1;
        }

        public NpcData Get(int id) { return Data.Npcs.FirstOrDefault(n => n.Id == id); }
    }

    // ───────────────────────────── PNJ vivant dans le monde ─────────────────────────────
    public class NpcInstance
    {
        public NpcData Data;
        public GameObject Go;
        public CharacterSetup Setup;
        public bool Alive { get { return Go != null; } }
    }

    // ───────────────────────────── SPAWN / DESPAWN ─────────────────────────────
    public class NpcManager
    {
        private readonly DataStore store;
        public readonly Dictionary<int, NpcInstance> Instances = new Dictionary<int, NpcInstance>();
        private readonly Dictionary<int, int> failures = new Dictionary<int, int>();
        private float nextCheck;

        // Scripts "joueur" à couper sur un PNJ (il n'y a personne pour les piloter).
        private static readonly HashSet<string> ServerDisabledBehaviours = new HashSet<string>
        {
            "AnimationControl", "AdvancedWalkerController", "SimpleWalkerController", "Mover",
            "CharacterKeyboardInput", "CharacterJoystickInput", "TurnTowardControllerVelocity"
        };

        public NpcManager(DataStore store) { this.store = store; }

        // LifeNetworkManager a son propre champ statique "singleton" (distinct de celui de Mirror).
        public static LifeNetworkManager Lnm
        {
            get
            {
                var nm = LifeNetworkManager.singleton;
                if (nm == null) nm = NetworkManager.singleton as LifeNetworkManager;
                return nm;
            }
        }

        // Le serveur est prêt quand Mirror tourne ET que les prefabs joueurs existent.
        public static bool ServerReady
        {
            get
            {
                if (!NetworkServer.active) return false;
                var nm = Lnm;
                return nm != null && nm.malePrefab != null && nm.femalePrefab != null;
            }
        }

        public void ResetFailures() { failures.Clear(); }

        public void SpawnAll()
        {
            foreach (var n in store.Data.Npcs.ToList()) Spawn(n);
            Debug.Log("[NovaPnj] " + Instances.Count + " PNJ chargé(s).");
        }

        public void DespawnAll()
        {
            foreach (var id in Instances.Keys.ToList()) Despawn(id);
        }

        public NpcInstance Spawn(NpcData d)
        {
            if (d == null) return null;
            Despawn(d.Id);
            if (!d.Enabled || !ServerReady) return null;
            try
            {
                var nm = Lnm;
                var prefab = d.Sex == 1 ? nm.malePrefab : nm.femalePrefab;
                var go = UnityEngine.Object.Instantiate(prefab, d.Position, Quaternion.Euler(0f, d.RotationY, 0f));
                go.name = "NovaPnj_" + d.Id;
                go.transform.localScale = Vector3.one * d.Scale;

                var setup = go.GetComponent<CharacterSetup>();
                if (setup == null)
                {
                    UnityEngine.Object.Destroy(go);
                    Debug.LogError("[NovaPnj] Le prefab joueur n'a pas de CharacterSetup : version du jeu incompatible ?");
                    return null;
                }

                var skin = SkinOf(d);
                if (skin != null) SetSyncVar(setup, "NetworkcharacterSkinData", skin);
                setup.NetworkFullName = d.Name;
                SetSyncVar(setup, "NetworkHealth", 100);
                SetSyncVar(setup, "NetworkHunger", 100);
                SetSyncVar(setup, "NetworkThirst", 100);
                SetSyncVar(setup, "NetworkusernameColor", Nova.HexToColor(Cfg.NameColorHex));

                var inst = new NpcInstance { Data = d, Go = go, Setup = setup };
                ForceServerAuthority(go);
                NetworkServer.Spawn(go, (NetworkConnection)null);
                Neutralize(inst);
                Instances[d.Id] = inst;
                failures.Remove(d.Id);
                return inst;
            }
            catch (Exception e)
            {
                int n; failures.TryGetValue(d.Id, out n); failures[d.Id] = n + 1;
                Debug.LogError("[NovaPnj] Impossible de faire apparaître le PNJ " + d.Id + " (" + d.Name + ") : " + e);
                return null;
            }
        }

        public void Despawn(int id)
        {
            NpcInstance inst;
            if (!Instances.TryGetValue(id, out inst)) return;
            Instances.Remove(id);
            if (inst.Go == null) return;
            try
            {
                if (NetworkServer.active) NetworkServer.Destroy(inst.Go);
                else UnityEngine.Object.Destroy(inst.Go);
            }
            catch (Exception e) { Debug.LogWarning("[NovaPnj] Erreur à la suppression du PNJ " + id + " : " + e.Message); }
        }

        public NpcInstance Respawn(NpcData d) { return Spawn(d); }

        // Appelée à chaque frame par le Runner : remet sur pied les PNJ disparus.
        public void Tick(float now)
        {
            if (now < nextCheck) return;
            nextCheck = now + Cfg.RespawnCheckSeconds;
            if (!ServerReady) return;
            foreach (var d in store.Data.Npcs.ToList())
            {
                if (!d.Enabled) continue;
                NpcInstance inst;
                bool known = Instances.TryGetValue(d.Id, out inst);
                if (known && inst.Alive) continue;
                int f; failures.TryGetValue(d.Id, out f);
                if (known)
                {
                    // Le PNJ a été détruit par le jeu : on compte, pour ne pas le recréer en boucle.
                    Instances.Remove(d.Id);
                    failures[d.Id] = ++f;
                    Debug.LogWarning("[NovaPnj] Le PNJ " + d.Id + " (" + d.Name + ") a disparu (" + f + "/" + Cfg.MaxSpawnFailures + ").");
                }
                if (f >= Cfg.MaxSpawnFailures) continue;
                Spawn(d);
            }
        }

        public NpcData Nearest(Player player)
        {
            var pos = player.setup.transform.position;
            return store.Data.Npcs
                .Where(n => n.Enabled)
                .Select(n => new { n, dist = Vector3.Distance(pos, n.Position) })
                .Where(x => x.dist <= x.n.InteractRange)
                .OrderBy(x => x.dist).Select(x => x.n).FirstOrDefault();
        }

        // ───────── apparence ─────────
        public static CharacterCustomizationSetup SkinOf(NpcData d)
        {
            if (d == null || string.IsNullOrEmpty(d.Skin)) return null;
            try { return CharacterCustomizationSetup.DeserializeFromJson(d.Skin); }
            catch (Exception e) { Debug.LogWarning("[NovaPnj] Apparence illisible pour le PNJ " + d.Id + " : " + e.Message); return null; }
        }

        public static void StoreSkin(NpcData d, CharacterCustomizationSetup s)
        {
            d.Skin = s == null ? "" : s.SerializeToJson();
        }

        public static CharacterCustomizationSetup CloneSkin(CharacterCustomizationSetup s)
        {
            return s == null ? null : CharacterCustomizationSetup.DeserializeFromJson(s.SerializeToJson());
        }

        /// <summary>Modifie l'apparence, sauvegarde, et applique en direct (sans respawn).</summary>
        public void EditSkin(NpcData d, Action<CharacterCustomizationSetup> edit)
        {
            var s = SkinOf(d) ?? new CharacterCustomizationSetup();
            edit(s);
            StoreSkin(d, s);
            store.Save();
            ApplySkin(d);
        }

        public void ApplySkin(NpcData d)
        {
            NpcInstance inst;
            if (!Instances.TryGetValue(d.Id, out inst) || !inst.Alive) return;
            var s = SkinOf(d);
            if (s == null) return;
            SetSyncVar(inst.Setup, "NetworkcharacterSkinData", s);
            try { inst.Setup.RpcSkinChange(s); } catch (Exception e) { Debug.LogWarning("[NovaPnj] RpcSkinChange : " + e.Message); }
        }

        public void ApplyName(NpcData d)
        {
            NpcInstance inst;
            if (Instances.TryGetValue(d.Id, out inst) && inst.Alive) inst.Setup.NetworkFullName = d.Name;
        }

        // ───────── bricolage Mirror / réflexion (même méthode que PNJCreator) ─────────
        // La propriété Network... existe parfois en double dans la DLL : on passe par la réflexion.
        private static void SetSyncVar(CharacterSetup setup, string name, object value)
        {
            foreach (var p in typeof(CharacterSetup).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != name || !p.CanWrite) continue;
                try
                {
                    object v = value;
                    if (v != null && v is IConvertible && p.PropertyType != v.GetType() && typeof(IConvertible).IsAssignableFrom(p.PropertyType))
                        v = Convert.ChangeType(v, p.PropertyType);
                    p.SetValue(setup, v, null);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[NovaPnj] " + name + " : " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                }
                return;
            }
            Debug.LogWarning("[NovaPnj] SyncVar " + name + " introuvable (version du jeu différente ?).");
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
            }
            return null;
        }

        private static object GetMember(object o, string name)
        {
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanRead) return p.GetValue(o, null);
            var f = FindField(t, name);
            return f == null ? null : f.GetValue(o);
        }

        private static void SetMember(object o, string name, object value)
        {
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) { p.SetValue(o, value, null); return; }
                var f = FindField(t, name);
                if (f != null) f.SetValue(o, value);
            }
            catch (Exception e) { Debug.LogWarning("[NovaPnj] " + o.GetType().Name + "." + name + " : " + e.Message); }
        }

        // Le serveur est seul maître de la position / animation du PNJ (aucun client ne le pilote).
        private static void ForceServerAuthority(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if (!t.Name.Contains("NetworkTransform") && t.Name != "NetworkAnimator") continue;
                var sd = FindField(t, "syncDirection");
                if (sd != null && sd.FieldType.IsEnum && Enum.IsDefined(sd.FieldType, "ServerToClient"))
                    sd.SetValue(c, Enum.Parse(sd.FieldType, "ServerToClient"));
                var ca = FindField(t, "clientAuthority");
                if (ca != null && ca.FieldType == typeof(bool)) ca.SetValue(c, false);
            }
        }

        // Coupe les scripts de contrôle joueur, fige la physique, pose l'animation "au repos".
        private static void Neutralize(NpcInstance inst)
        {
            inst.Setup.CancelInvoke();
            foreach (var b in inst.Go.GetComponentsInChildren<MonoBehaviour>(true))
                if (b != null && ServerDisabledBehaviours.Contains(b.GetType().Name)) b.enabled = false;

            var rb = inst.Go.GetComponent("Rigidbody");
            if (rb != null)
            {
                SetMember(rb, "isKinematic", true);
                SetMember(rb, "useGravity", false);
            }

            foreach (var c in inst.Go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "NetworkAnimator") continue;
                var anim = GetMember(c, "animator");
                if (anim == null) continue;
                var tt = anim.GetType();
                var setFloat = tt.GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                var setBool = tt.GetMethod("SetBool", new[] { typeof(string), typeof(bool) });
                try
                {
                    if (setFloat != null)
                    {
                        setFloat.Invoke(anim, new object[] { "ForwardSpeed", 0f });
                        setFloat.Invoke(anim, new object[] { "HorizontalSpeed", 0f });
                    }
                    if (setBool != null) setBool.Invoke(anim, new object[] { "IsGrounded", true });
                }
                catch (Exception) { }
            }
        }
    }

    // ───────────────────────────── RUNNER (thread principal Unity) ─────────────────────────────
    public class NpcRunner : MonoBehaviour
    {
        public NovaPnjPlugin Plugin;
        private bool spawned;
        private float readySince = -1f;

        private void Update()
        {
            if (Plugin == null) return;
            try
            {
                if (!spawned)
                {
                    if (!NpcManager.ServerReady) { readySince = -1f; return; }
                    if (readySince < 0f) readySince = Time.time;
                    if (Time.time - readySince < Cfg.SpawnDelaySeconds) return;
                    spawned = true;
                    Plugin.Npcs.SpawnAll();
                    return;
                }
                Plugin.Npcs.Tick(Time.time);
            }
            catch (Exception e) { Debug.LogError("[NovaPnj] Runner : " + e); }
        }

        public void ForceRespawn() { spawned = false; readySince = -1f; }
        private void OnApplicationQuit() { if (Plugin != null) Plugin.Store.Save(); }
    }

    // ───────────────────────────── MENUS ─────────────────────────────
    public class Menus
    {
        private readonly NovaPnjPlugin P;
        public Menus(NovaPnjPlugin plugin) { P = plugin; }
        private DataStore Store { get { return P.Store; } }
        private NpcManager Npcs { get { return P.Npcs; } }

        // ── helpers ──
        public static void Tell(Player p, string msg, NotificationManager.Type type)
        {
            p.Notify(Cfg.PluginTitle, msg, type, 5f);
        }

        public static bool CanEdit(Player p)
        {
            if (!Cfg.RequireAdmin) return true;
            if (p == null || p.account == null || !p.IsAdmin) return false;
            if (p.account.AdminLevel < Cfg.RequiredAdminLevel) return false;
            if (Cfg.RequireAdminService && !p.IsAdminService) return false;
            return true;
        }

        public bool Guard(Player p)
        {
            if (CanEdit(p)) return true;
            Tell(p, Cfg.RequireAdminService && p != null && p.IsAdmin
                ? "Vous devez être en service admin."
                : "Réservé aux administrateurs (niveau " + Cfg.RequiredAdminLevel + " minimum).", NotificationManager.Type.Error);
            return false;
        }

        private static void Close(Player p, UIPanel u) { p.ClosePanel(u); }

        private void Ask(Player p, string title, string text, string placeholder, Action<string> ok, Action back)
        {
            var ui = new UIPanel(title, UIPanel.PanelType.Input).SetText(text).SetInputPlaceholder(placeholder);
            ui.AddButton("Valider", u => { var t = (u.inputText ?? "").Trim(); Close(p, u); ok(t); });
            ui.AddButton("Retour", u => { Close(p, u); back(); });
            p.ShowPanelUI(ui);
        }

        private void Confirm(Player p, string text, Action yes, Action no)
        {
            var ui = new UIPanel("Confirmer", UIPanel.PanelType.Text).SetText(text);
            ui.AddButton("Confirmer", u => { Close(p, u); yes(); });
            ui.AddButton("Annuler", u => { Close(p, u); no(); });
            p.ShowPanelUI(ui);
        }

        private void Tab(Player p, string title, string text, List<KeyValuePair<string, Action>> lines, Action back)
        {
            var ui = new UIPanel(title, UIPanel.PanelType.Tab);
            if (!string.IsNullOrEmpty(text)) ui.SetText(text);
            foreach (var l in lines)
            {
                var act = l.Value;
                ui.AddTabLine(l.Key, u => { Close(p, u); act(); });
            }
            ui.AddButton("Sélectionner", u => u.SelectTab());
            if (back != null) ui.AddButton("Retour", u => { Close(p, u); back(); });
            ui.AddButton("Fermer", u => Close(p, u));
            p.ShowPanelUI(ui);
        }

        private static KeyValuePair<string, Action> L(string label, Action a) { return new KeyValuePair<string, Action>(label, a); }

        private static float? ParseFloat(string t)
        {
            float v;
            if (float.TryParse((t ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        private static string F(float v) { return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
        private static string Cut(string s, int n) { return s.Length > n ? s.Substring(0, n) + "…" : s; }
        private static Vector3 PosOf(Player p) { return p.setup.transform.position; }
        private static float RotOf(Player p) { return p.setup.transform.eulerAngles.y; }

        // ═════════ MENU PRINCIPAL ═════════
        public void Main(Player p)
        {
            if (!Guard(p)) return;
            var lines = new List<KeyValuePair<string, Action>>();
            lines.Add(L("<color=#7CFC00>+ Créer un PNJ à ma position</color>", () => CreateName(p)));
            var pos = PosOf(p);
            foreach (var n in Store.Data.Npcs.OrderBy(x => x.Id))
            {
                var id = n.Id;
                var dist = Vector3.Distance(pos, n.Position);
                var state = !n.Enabled ? " (désactivé)" : (Npcs.Instances.ContainsKey(id) && Npcs.Instances[id].Alive ? "" : " (non spawné)");
                lines.Add(L("#" + n.Id + " " + n.Name + " · " + n.SexLabel + " · " + dist.ToString("0") + " m" + state, () => NpcMenu(p, id)));
            }
            Tab(p, Cfg.PluginTitle + " (" + Store.Data.Npcs.Count + ")", null, lines, null);
        }

        private void CreateName(Player p)
        {
            Ask(p, "Nouveau PNJ", "Nom du PNJ (affiché au-dessus de sa tête) :", "Ex. : Gérard le boulanger",
                name => { if (name == "") { Main(p); return; } CreateSex(p, name); }, () => Main(p));
        }

        private void CreateSex(Player p, string name)
        {
            Tab(p, "Sexe de « " + name + " »", null, new List<KeyValuePair<string, Action>>
            {
                L("Homme", () => Create(p, name, 1)),
                L("Femme", () => Create(p, name, 0))
            }, () => CreateName(p));
        }

        private void Create(Player p, string name, int sex)
        {
            if (!Guard(p)) return;
            if (!NpcManager.ServerReady) { Tell(p, "Le serveur n'est pas encore prêt, réessayez dans quelques secondes.", NotificationManager.Type.Warning); return; }

            var d = new NpcData { Id = Store.NextId(), Name = name, Sex = sex, RotationY = RotOf(p) };
            d.Position = PosOf(p);

            // apparence de départ = celle de l'admin, adaptée au sexe choisi
            var skin = NpcManager.CloneSkin(p.setup.characterSkinData) ?? new CharacterCustomizationSetup();
            bool adminIsMale = false;
            try { adminIsMale = p.character != null && p.character.SexId == 0; } catch (Exception) { }
            if (adminIsMale != (sex == 1)) ResetForSex(skin, sex == 1);
            NpcManager.StoreSkin(d, skin);

            Store.Data.Npcs.Add(d);
            Store.Save();
            Npcs.Spawn(d);
            Tell(p, "PNJ « " + name + " » créé (#" + d.Id + ").", NotificationManager.Type.Success);
            NpcMenu(p, d.Id);
        }

        // ═════════ FICHE D'UN PNJ ═════════
        public void NpcMenu(Player p, int id)
        {
            if (!Guard(p)) return;
            var d = Store.Get(id);
            if (d == null) { Tell(p, "Ce PNJ n'existe plus.", NotificationManager.Type.Warning); Main(p); return; }
            Action me = () => NpcMenu(p, id);

            var lines = new List<KeyValuePair<string, Action>>();
            lines.Add(L("Renommer (" + d.Name + ")", () => Ask(p, "Renommer", "Nouveau nom :", d.Name, t =>
            {
                if (t != "") { d.Name = t; Store.Save(); Npcs.ApplyName(d); }
                me();
            }, me)));
            lines.Add(L("Sous-titre : " + (string.IsNullOrEmpty(d.Title) ? "—" : d.Title), () => Ask(p, "Sous-titre", "Texte affiché quand on lui parle :", d.Title, t =>
            {
                d.Title = t; Store.Save(); me();
            }, me)));
            lines.Add(L("Sexe : " + d.SexLabel + " (changer)", () => Confirm(p,
                "Passer « " + d.Name + " » en " + (d.Sex == 1 ? "femme" : "homme") + " ? Coupe et tenue seront réinitialisées.", () =>
                {
                    d.Sex = d.Sex == 1 ? 0 : 1;
                    var s = NpcManager.SkinOf(d) ?? new CharacterCustomizationSetup();
                    ResetForSex(s, d.Sex == 1);
                    NpcManager.StoreSkin(d, s);
                    Store.Save(); Npcs.Respawn(d); me();
                }, me)));
            lines.Add(L("Échelle : x" + F(d.Scale), () => Ask(p, "Échelle", "Taille du PNJ (0.5 à 2) :", F(d.Scale), t =>
            {
                var v = ParseFloat(t);
                if (v.HasValue && v.Value >= 0.5f && v.Value <= 2f) { d.Scale = v.Value; Store.Save(); Npcs.Respawn(d); }
                else Tell(p, "Valeur invalide (0.5 à 2).", NotificationManager.Type.Warning);
                me();
            }, me)));
            lines.Add(L("Déplacer à ma position", () =>
            {
                d.Position = PosOf(p); d.RotationY = RotOf(p);
                Store.Save(); Npcs.Respawn(d);
                Tell(p, "Le PNJ a été déplacé.", NotificationManager.Type.Success); me();
            }));
            lines.Add(L("Tourner vers moi", () =>
            {
                var dir = PosOf(p) - d.Position; dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) d.RotationY = Quaternion.LookRotation(dir).eulerAngles.y;
                Store.Save(); Npcs.Respawn(d); me();
            }));
            lines.Add(L("Rotation : " + d.RotationY.ToString("0") + "°", () => Ask(p, "Rotation", "Angle en degrés (0 à 360) :", d.RotationY.ToString("0"), t =>
            {
                var v = ParseFloat(t);
                if (v.HasValue) { d.RotationY = Mathf.Repeat(v.Value, 360f); Store.Save(); Npcs.Respawn(d); }
                me();
            }, me)));
            lines.Add(L("Portée de /pnj : " + F(d.InteractRange) + " m", () => Ask(p, "Portée", "Distance en mètres (1 à 50) :", F(d.InteractRange), t =>
            {
                var v = ParseFloat(t);
                if (v.HasValue && v.Value >= 1f && v.Value <= 50f) { d.InteractRange = v.Value; Store.Save(); }
                else Tell(p, "Valeur invalide (1 à 50).", NotificationManager.Type.Warning);
                me();
            }, me)));
            lines.Add(L(d.Enabled ? "Désactiver ce PNJ" : "Activer ce PNJ", () =>
            {
                d.Enabled = !d.Enabled; Store.Save();
                if (d.Enabled) Npcs.Spawn(d); else Npcs.Despawn(d.Id);
                me();
            }));
            lines.Add(L("Interactions texte (" + d.Lines.Count + ")…", () => LinesMenu(p, id)));
            lines.Add(L("Apparence / costume…", () => LookMenu(p, id)));
            lines.Add(L("Faire réapparaître", () =>
            {
                Npcs.ResetFailures(); Npcs.Respawn(d);
                Tell(p, "PNJ respawn.", NotificationManager.Type.Info); me();
            }));
            lines.Add(L("<color=#FF5555>Supprimer ce PNJ</color>", () => Confirm(p, "Supprimer définitivement « " + d.Name + " » (#" + d.Id + ") ?", () =>
            {
                Npcs.Despawn(d.Id); Store.Data.Npcs.Remove(d); Store.Save();
                Tell(p, "PNJ supprimé.", NotificationManager.Type.Success); Main(p);
            }, me)));

            Tab(p, "PNJ #" + d.Id + " — " + d.Name, d.SexLabel + " | " + (d.Enabled ? "actif" : "désactivé"), lines, () => Main(p));
        }

        // ═════════ INTERACTIONS TEXTE ═════════
        private void LinesMenu(Player p, int id)
        {
            var d = Store.Get(id); if (d == null) { Main(p); return; }
            Action me = () => LinesMenu(p, id);
            var lines = new List<KeyValuePair<string, Action>>();
            lines.Add(L("<color=#7CFC00>+ Ajouter une interaction</color>", () => Ask(p, "Nouveau texte", "Ce que dit le PNJ :", "Bonjour !", t =>
            {
                if (t != "") { d.Lines.Add(t); Store.Save(); }
                me();
            }, me)));
            for (int i = 0; i < d.Lines.Count; i++)
            {
                var idx = i;
                lines.Add(L((i + 1) + ". " + Cut(d.Lines[i], 45), () => LineActions(p, id, idx)));
            }
            Tab(p, "Interactions — " + d.Name, null, lines, () => NpcMenu(p, id));
        }

        private void LineActions(Player p, int id, int idx)
        {
            var d = Store.Get(id); if (d == null || idx < 0 || idx >= d.Lines.Count) { LinesMenu(p, id); return; }
            var ui = new UIPanel("Interaction " + (idx + 1), UIPanel.PanelType.Text).SetText(d.Lines[idx]);
            ui.AddButton("Modifier", u => { Close(p, u); Ask(p, "Modifier le texte", "Nouveau texte :", d.Lines[idx], t =>
            {
                if (t != "") { d.Lines[idx] = t; Store.Save(); }
                LinesMenu(p, id);
            }, () => LinesMenu(p, id)); });
            ui.AddButton("Supprimer", u => { Close(p, u); d.Lines.RemoveAt(idx); Store.Save(); LinesMenu(p, id); });
            ui.AddButton("Retour", u => { Close(p, u); LinesMenu(p, id); });
            p.ShowPanelUI(ui);
        }

        // ═════════ APPARENCE ═════════
        private static bool IsMaleId(object sexId)
        {
            // Dans le jeu, SexId 0 = homme (malePrefab), 1 = femme.
            try { return Convert.ToInt32(sexId) == 0; } catch (Exception) { return false; }
        }

        private static int FirstCloth(int type, bool male)
        {
            try
            {
                return Nova.server.buyableCloths
                    .Where(c => Convert.ToInt32(c.clothType) == type && IsMaleId(c.sexId) == male)
                    .Select(c => Convert.ToInt32(c.clothId)).DefaultIfEmpty(-1).First();
            }
            catch (Exception) { return -1; }
        }

        // Remet une apparence "propre" pour le sexe voulu (coupe, tenue de base, sans flocage).
        private static void ResetForSex(CharacterCustomizationSetup s, bool male)
        {
            try
            {
                s.Hair = Nova.server.hairCuts.Where(h => IsMaleId(h.sexId) == male)
                    .Select(h => Convert.ToInt32(h.hairId)).DefaultIfEmpty(0).First();
            }
            catch (Exception) { }
            s.Beard = -1; s.Hat = -1; s.Accessory = -1;
            s.TShirt = FirstCloth(2, male);
            s.Pants = FirstCloth(3, male);
            s.Shoes = FirstCloth(4, male);
            s.tshirtData = new ClothData { clothName = "", url = "" };
            s.pantsData = new ClothData { clothName = "", url = "" };
            if (male) s.BreastSize = 0f;
        }

        private static List<FieldInfo> Fields(Type t)
        {
            return typeof(CharacterCustomizationSetup).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == t).ToList();
        }

        private static bool TrySet(CharacterCustomizationSetup s, FieldInfo f, string text)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            int i; float v;
            if (f.FieldType == typeof(int) && int.TryParse(text, out i)) { f.SetValue(s, i); return true; }
            if (f.FieldType == typeof(float) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) { f.SetValue(s, v); return true; }
            return false;
        }

        private CharacterCustomizationSetup ClosestLook(Player p)
        {
            var pos = PosOf(p);
            Player other = null;
            try
            {
                other = Nova.server.Players
                    .Where(o => o != null && o != p && o.setup != null)
                    .OrderBy(o => Vector3.Distance(pos, o.setup.transform.position))
                    .FirstOrDefault();
            }
            catch (Exception) { }
            if (other == null)
            {
                Tell(p, "Aucun autre joueur à proximité.", NotificationManager.Type.Warning);
                return null;
            }
            return NpcManager.CloneSkin(other.setup.characterSkinData);
        }

        private void LookMenu(Player p, int id)
        {
            var d = Store.Get(id); if (d == null) { Main(p); return; }
            Action me = () => LookMenu(p, id);
            var lines = new List<KeyValuePair<string, Action>>();
            lines.Add(L("<color=#7CFC00>Copier MON apparence</color>", () =>
            {
                var c = NpcManager.CloneSkin(p.setup.characterSkinData);
                if (c != null) { Npcs.EditSkin(d, s => CopyInto(s, c)); Tell(p, "Apparence copiée.", NotificationManager.Type.Success); }
                me();
            }));
            lines.Add(L("Copier le joueur le plus proche", () =>
            {
                var c = ClosestLook(p);
                if (c != null) { Npcs.EditSkin(d, s => CopyInto(s, c)); Tell(p, "Apparence copiée.", NotificationManager.Type.Success); }
                me();
            }));
            lines.Add(L("Repartir d'une tenue de base", () =>
            {
                Npcs.EditSkin(d, s => ResetForSex(s, d.Sex == 1)); me();
            }));
            lines.Add(L("Éléments : cheveux, barbe, vêtements (IDs)…", () => FieldsMenu(p, id, Fields(typeof(int)), "Éléments")));
            lines.Add(L("Morphologie et visage (curseurs)…", () => FieldsMenu(p, id, Fields(typeof(float)), "Morphologie")));
            lines.Add(L("Choisir un vêtement du jeu…", () => ClothSlotMenu(p, id)));
            lines.Add(L("Flocage haut (URL image)", () => FlockFlow(p, id, true)));
            lines.Add(L("Flocage bas (URL image)", () => FlockFlow(p, id, false)));
            lines.Add(L("Retirer les flocages", () =>
            {
                Npcs.EditSkin(d, s =>
                {
                    s.tshirtData = new ClothData { clothName = "", url = "" };
                    s.pantsData = new ClothData { clothName = "", url = "" };
                });
                me();
            }));
            Tab(p, "Apparence — " + d.Name, null, lines, () => NpcMenu(p, id));
        }

        // Copie tous les champs publics (visage, corps, vêtements, flocages) d'une apparence vers une autre.
        private static void CopyInto(CharacterCustomizationSetup dst, CharacterCustomizationSetup src)
        {
            foreach (var f in typeof(CharacterCustomizationSetup).GetFields(BindingFlags.Public | BindingFlags.Instance))
                try { f.SetValue(dst, f.GetValue(src)); } catch (Exception) { }
        }

        private void FieldsMenu(Player p, int id, List<FieldInfo> fields, string title)
        {
            var d = Store.Get(id); if (d == null) { Main(p); return; }
            var skin = NpcManager.SkinOf(d) ?? new CharacterCustomizationSetup();
            Action me = () => FieldsMenu(p, id, fields, title);
            var lines = new List<KeyValuePair<string, Action>>();
            foreach (var f in fields)
            {
                var field = f;
                var cur = field.GetValue(skin);
                lines.Add(L(field.Name + " = " + cur, () => Ask(p, field.Name, "Valeur actuelle : " + cur, cur == null ? "" : cur.ToString(), t =>
                {
                    bool ok = true;
                    Npcs.EditSkin(d, s => { ok = TrySet(s, field, t); });
                    if (!ok) Tell(p, "Valeur invalide.", NotificationManager.Type.Warning);
                    me();
                }, me)));
            }
            Tab(p, title + " — " + d.Name, null, lines, () => LookMenu(p, id));
        }

        private static readonly string[] SlotNames = { "Chapeau", "Accessoire", "Haut", "Bas", "Chaussures" };

        private void ClothSlotMenu(Player p, int id)
        {
            var lines = new List<KeyValuePair<string, Action>>();
            for (int i = 0; i < SlotNames.Length; i++)
            {
                var slot = i;
                lines.Add(L(SlotNames[i], () => ClothListMenu(p, id, slot)));
            }
            Tab(p, "Type de vêtement", null, lines, () => LookMenu(p, id));
        }

        private void ClothListMenu(Player p, int id, int slot)
        {
            var d = Store.Get(id); if (d == null) { Main(p); return; }
            bool male = d.Sex == 1;
            Action me = () => ClothListMenu(p, id, slot);
            var lines = new List<KeyValuePair<string, Action>>();
            lines.Add(L("Aucun (retirer)", () =>
            {
                Npcs.EditSkin(d, s => SetSlot(s, slot, -1)); LookMenu(p, id);
            }));
            try
            {
                foreach (var c in Nova.server.buyableCloths
                    .Where(c => Convert.ToInt32(c.clothType) == slot && IsMaleId(c.sexId) == male).Take(Cfg.MaxListed))
                {
                    var clothId = Convert.ToInt32(c.clothId);
                    var name = string.IsNullOrEmpty(c.name) ? "n°" + clothId : c.name;
                    lines.Add(L("#" + clothId + " " + name, () =>
                    {
                        Npcs.EditSkin(d, s => SetSlot(s, slot, clothId)); LookMenu(p, id);
                    }));
                }
            }
            catch (Exception e) { Debug.LogWarning("[NovaPnj] Liste des vêtements : " + e.Message); }
            Tab(p, SlotNames[slot] + " — " + d.Name, null, lines, () => ClothSlotMenu(p, id));
        }

        private static void SetSlot(CharacterCustomizationSetup s, int slot, int clothId)
        {
            switch (slot)
            {
                case 0: s.Hat = clothId; break;
                case 1: s.Accessory = clothId; break;
                case 2: s.TShirt = clothId; s.tshirtData = new ClothData { clothName = "", url = "" }; break;
                case 3: s.Pants = clothId; s.pantsData = new ClothData { clothName = "", url = "" }; break;
                case 4: s.Shoes = clothId; break;
            }
        }

        private void FlockFlow(Player p, int id, bool top)
        {
            var d = Store.Get(id); if (d == null) { Main(p); return; }
            Action back = () => LookMenu(p, id);
            Ask(p, "Flocage " + (top ? "haut" : "bas"), "Nom du vêtement :", "Ex. : Veste de shérif", name =>
            {
                if (name == "") { back(); return; }
                Ask(p, "Image du " + (top ? "haut" : "bas"), "Lien direct de l'image (http/https, .png ou .jpg) :", "https://…", url =>
                {
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Tell(p, "Lien invalide.", NotificationManager.Type.Warning); back(); return; }
                    Npcs.EditSkin(d, s =>
                    {
                        var data = new ClothData { clothName = name, url = url };
                        if (top) s.tshirtData = data; else s.pantsData = data;
                    });
                    back();
                }, back);
            }, back);
        }

        // ═════════ JOUEUR : parler au PNJ proche ═════════
        public void Talk(Player p)
        {
            var d = Npcs.Nearest(p);
            if (d == null) { Tell(p, "Aucun PNJ à proximité.", NotificationManager.Type.Info); return; }
            if (d.Lines.Count == 0) { p.Notify(d.Name, "…", NotificationManager.Type.Info, 5f); return; }
            var lines = new List<KeyValuePair<string, Action>>();
            foreach (var t in d.Lines.ToList())
            {
                var text = t;
                lines.Add(L(Cut(text, 40), () => Speech(p, d, text)));
            }
            Tab(p, d.Name + (string.IsNullOrEmpty(d.Title) ? "" : " — " + d.Title), null, lines, null);
        }

        private void Speech(Player p, NpcData d, string text)
        {
            var ui = new UIPanel(d.Name, UIPanel.PanelType.Text).SetText(text);
            ui.AddButton("Retour", u => { Close(p, u); Talk(p); });
            ui.AddButton("Fermer", u => Close(p, u));
            p.ShowPanelUI(ui);
        }

        // ═════════ DEBUG ═════════
        public void Debug_(Player p)
        {
            if (!Guard(p)) return;
            var nm = NpcManager.Lnm;
            Tell(p, "Serveur prêt : " + NpcManager.ServerReady
                + " | prefabs : " + (nm != null && nm.malePrefab != null) + "/" + (nm != null && nm.femalePrefab != null)
                + " | PNJ en base : " + Store.Data.Npcs.Count + " | spawnés : " + Npcs.Instances.Count(kv => kv.Value.Alive), NotificationManager.Type.Info);
            var pos = PosOf(p);
            var near = Store.Data.Npcs.OrderBy(n => Vector3.Distance(pos, n.Position)).FirstOrDefault();
            if (near == null) return;
            NpcInstance inst;
            bool alive = Npcs.Instances.TryGetValue(near.Id, out inst) && inst.Alive;
            Tell(p, "Plus proche : #" + near.Id + " " + near.Name + " à " + Vector3.Distance(pos, near.Position).ToString("0.0") + " m | "
                + (alive ? "spawné (netId " + inst.Go.GetComponent<NetworkIdentity>().netId + ")" : "NON spawné"), NotificationManager.Type.Info);
        }
    }

    // ───────────────────────────── PLUGIN ─────────────────────────────
    public class NovaPnjPlugin : Plugin
    {
        public DataStore Store;
        public NpcManager Npcs;
        public Menus Menus;
        public NpcRunner Runner;

        public NovaPnjPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Store = new DataStore(pluginsPath);
            Store.Load();
            Npcs = new NpcManager(Store);
            Menus = new Menus(this);

            // Objet Unity persistant : spawn des PNJ dès que le serveur est prêt + vérification régulière.
            var go = new GameObject("NovaPnjRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Runner = go.AddComponent<NpcRunner>();
            Runner.Plugin = this;

            new SChatCommand(Cfg.CmdAdmin, Cfg.CmdAdminAliases, "Éditeur des PNJ (admin)", Cfg.CmdAdmin,
                (Action<Player, string[]>)((player, args) => { if (Menus.Guard(player)) Menus.Main(player); })).Register();

            new SChatCommand(Cfg.CmdTalk, "Parler au PNJ à proximité", Cfg.CmdTalk,
                (Action<Player, string[]>)((player, args) => Menus.Talk(player))).Register();

            new SChatCommand(Cfg.CmdReload, "Recharge les PNJ depuis le fichier (admin)", Cfg.CmdReload,
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!Menus.Guard(player)) return;
                    Npcs.DespawnAll();
                    Store.Load();
                    Npcs.ResetFailures();
                    Runner.ForceRespawn();
                    Menus.Tell(player, "Rechargé : " + Store.Data.Npcs.Count + " PNJ.", NotificationManager.Type.Success);
                })).Register();

            new SChatCommand(Cfg.CmdDebug, "Infos de debug des PNJ (admin)", Cfg.CmdDebug,
                (Action<Player, string[]>)((player, args) => Menus.Debug_(player))).Register();

            Debug.Log("[NovaPnj] " + Cfg.PluginTitle + " v" + Cfg.Version + " — chargé (" + Store.Data.Npcs.Count + " PNJ, fichier : " + Store.FilePath + ").");
        }
    }
}
