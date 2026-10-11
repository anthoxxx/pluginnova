using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Life;
using Life.BizSystem;
using Life.Network;
using Life.UI;
using Life.VehicleSystem;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _menu = AAMenu.Menu;

namespace Contravention
{
    /// <summary>
    /// Contraventions : les membres d'un métier (entreprise configurée dans config.json)
    /// verbalisent les véhicules proches. Le véhicule reste bloqué tant que le propriétaire
    /// n'a pas payé (menu AA > Interaction > Mes contraventions).
    /// </summary>
    public class ContraventionPlugin : ModKit.ModKit
    {
        public static ContraventionPlugin Instance;
        public ContraventionConfig Config;
        public FineStore Store;

        public ContraventionPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations("Contravention", "1.0.0", "Alfred");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            string dir = Path.Combine(PluginPath ?? Directory.GetCurrentDirectory(), "Contravention");
            Config = ContraventionConfig.Load(dir);
            Store = FineStore.Load(dir);
            GameApi.LogDiagnostics();

            GameObject go = new GameObject("ContraventionEnforcer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FineEnforcer>();

            Nova.server.OnPlayerSpawnCharacterEvent += OnPlayerSpawn;

            // Menu AA > Entreprise (visible pour toutes les activités, réservé au BizId du config.json)
            List<Activity.Type> allActivities = Enum.GetValues(typeof(Activity.Type)).Cast<Activity.Type>().ToList();
            _menu.AddBizTabLine(PluginInformations, allActivities, null, "Contraventions", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player != null) OpenOfficerMenu(player);
            });

            // Menu AA > Interaction (tous les joueurs : payer ses contraventions)
            _menu.AddInteractionTabLine(PluginInformations, "Mes contraventions", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player != null) OpenOwnerMenu(player);
            });

            if (Config.BizId <= 0)
                Logger.LogWarning("Contravention", "BizId = 0 dans " + Path.Combine(dir, "config.json") + " : personne ne peut verbaliser. Mettez l'ID de l'entreprise puis redemarrez.");
            Logger.LogSuccess("Contravention v1.0.0", "initialise. BizId=" + Config.BizId + " | contraventions impayees=" + Store.All.Count);
        }

        private void OnPlayerSpawn(Player player)
        {
            try
            {
                List<Fine> mine = Store.ByOwner(player.character.Id);
                if (mine.Count > 0)
                    GameApi.Notify(player, "Contravention",
                        "Tu as " + mine.Count + " contravention(s) impayee(s) (" + mine.Sum(f => f.Amount) + " EUR). Menu AA > Interaction > Mes contraventions.",
                        GameApi.Warning);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private bool IsOfficer(Player p)
        {
            return Config.BizId > 0 && p.HasBiz && p.biz != null && p.biz.Id == Config.BizId;
        }

        // =====================================================================
        //  MENU METIER
        // =====================================================================
        private void OpenOfficerMenu(Player player)
        {
            if (!IsOfficer(player))
            {
                GameApi.Notify(player, "Contraventions", "Tu n'appartiens pas a ce metier.", GameApi.Error);
                return;
            }

            UIPanel panel = new UIPanel("Contraventions", GameApi.Tab);
            panel.AddTabLine("Donner une contravention", ui => OpenVehiclePicker(player));
            panel.AddTabLine("Contraventions impayees (" + Store.All.Count + ")", ui => OpenUnpaidList(player));
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        private void OpenVehiclePicker(Player player)
        {
            Vector3 pos = player.setup.transform.position;
            List<Vehicle> vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>()
                .Where(v => v != null && Vector3.Distance(v.transform.position, pos) <= Config.VehicleRadius)
                .OrderBy(v => Vector3.Distance(v.transform.position, pos))
                .ToList();

            if (vehicles.Count == 0)
            {
                ShowMessage(player, "Vehicule a verbaliser",
                    "Aucun vehicule a moins de " + (int)Config.VehicleRadius + " m.", () => OpenOfficerMenu(player));
                return;
            }

            UIPanel panel = new UIPanel("Vehicule a verbaliser", GameApi.Tab);
            foreach (Vehicle v in vehicles)
            {
                Vehicle veh = v;
                int dist = (int)Vector3.Distance(veh.transform.position, pos);
                string label = VehicleApi.GetPlate(veh) ?? "?";
                string name = SafeVehicleName(veh);
                if (!string.IsNullOrEmpty(name)) label += " - " + name;
                panel.AddTabLine(label + "  <color=#AAAAAA>(" + dist + " m)</color>", ui => AskAmount(player, veh));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => OpenOfficerMenu(player));
            player.ShowPanelUI(panel);
        }

        private void AskAmount(Player player, Vehicle veh)
        {
            UIPanel panel = new UIPanel("Montant (EUR)", GameApi.Input);
            panel.text = "Plaque : " + (VehicleApi.GetPlate(veh) ?? "?") + "\nMontant de la contravention (1 - " + Config.MaxAmount + ")";
            panel.inputPlaceholder = "Ex : 500";
            panel.AddButton("Suivant", ui =>
            {
                int amount;
                if (!int.TryParse((ui.inputText ?? "").Trim(), out amount) || amount < 1 || amount > Config.MaxAmount)
                {
                    GameApi.Notify(player, "Contraventions", "Montant invalide (1 - " + Config.MaxAmount + ").", GameApi.Error);
                    return;
                }
                AskReason(player, veh, amount);
            });
            panel.AddButton("Retour", ui => OpenVehiclePicker(player));
            player.ShowPanelUI(panel);
        }

        private void AskReason(Player player, Vehicle veh, int amount)
        {
            UIPanel panel = new UIPanel("Motif", GameApi.Input);
            panel.text = "Motif de la contravention de " + amount + " EUR";
            panel.inputPlaceholder = "Ex : Stationnement genant";
            panel.AddButton("Verbaliser", ui =>
            {
                string reason = (ui.inputText ?? "").Trim();
                if (reason.Length < 3)
                {
                    GameApi.Notify(player, "Contraventions", "Motif trop court.", GameApi.Error);
                    return;
                }
                if (reason.Length > 120) reason = reason.Substring(0, 120);
                player.ClosePanel(ui);
                IssueFine(player, veh, amount, reason);
            });
            panel.AddButton("Retour", ui => AskAmount(player, veh));
            player.ShowPanelUI(panel);
        }

        private void IssueFine(Player officer, Vehicle veh, int amount, string reason)
        {
            if (!IsOfficer(officer)) return;
            if (veh == null)
            {
                GameApi.Notify(officer, "Contraventions", "Vehicule introuvable.", GameApi.Error);
                return;
            }
            if (Vector3.Distance(veh.transform.position, officer.setup.transform.position) > Config.VehicleRadius + 5f)
            {
                GameApi.Notify(officer, "Contraventions", "Le vehicule est trop loin.", GameApi.Error);
                return;
            }

            int dbId = veh.VehicleDbId;
            if (dbId <= 0)
            {
                GameApi.Notify(officer, "Contraventions", "Ce vehicule n'est pas enregistre (pas de proprietaire).", GameApi.Error);
                return;
            }

            string plate = VehicleApi.GetPlate(veh) ?? "?";
            int ownerId = VehicleApi.GetOwnerCharacterId(veh);

            Fine fine = Store.Add(new Fine
            {
                VehicleDbId = dbId,
                Plate = plate,
                OwnerCharacterId = ownerId,
                Amount = amount,
                Reason = reason,
                IssuerName = officer.FullName,
                DateUtc = DateTime.UtcNow
            });

            VehicleApi.SetBlocked(veh, true); // blocage immediat, l'enforcer maintient ensuite

            GameApi.Notify(officer, "Contraventions",
                "Contravention #" + fine.Id + " de " + amount + " EUR donnee a " + plate + ". Vehicule bloque.",
                GameApi.Success);
            if (ownerId <= 0)
                GameApi.Notify(officer, "Contraventions", "Proprietaire introuvable : seul un agent pourra annuler cette contravention.", GameApi.Warning);

            Player owner = ownerId > 0 ? GameApi.OnlineByCharacterId(ownerId) : null;
            if (owner != null)
                GameApi.Notify(owner, "Contravention",
                    "Ton vehicule " + plate + " a recu une contravention de " + amount + " EUR (" + reason +
                    "). Il est bloque jusqu'au paiement : menu AA > Interaction > Mes contraventions.",
                    GameApi.Warning);
        }

        private void OpenUnpaidList(Player player)
        {
            if (!IsOfficer(player)) return;
            if (Store.All.Count == 0)
            {
                ShowMessage(player, "Contraventions impayees", "Aucune contravention impayee.", () => OpenOfficerMenu(player));
                return;
            }

            UIPanel panel = new UIPanel("Contraventions impayees", GameApi.Tab);
            foreach (Fine f in Store.All.OrderByDescending(x => x.DateUtc).Take(60))
            {
                Fine fine = f;
                panel.AddTabLine("#" + fine.Id + " " + fine.Plate + " - " + fine.Amount + " EUR", ui => OpenOfficerFineDetail(player, fine));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => OpenOfficerMenu(player));
            player.ShowPanelUI(panel);
        }

        private void OpenOfficerFineDetail(Player player, Fine fine)
        {
            UIPanel panel = new UIPanel("Contravention #" + fine.Id, GameApi.Text);
            panel.text =
                "Plaque : " + fine.Plate +
                "\nMontant : " + fine.Amount + " EUR" +
                "\nMotif : " + fine.Reason +
                "\nVerbalise par : " + fine.IssuerName +
                "\nDate : " + fine.DateUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            panel.AddButton("Annuler la contravention", ui =>
            {
                if (!IsOfficer(player)) return;
                if (Store.Remove(fine)) // le vehicule est debloque par l'enforcer s'il n'a plus d'amende
                    GameApi.Notify(player, "Contraventions", "Contravention #" + fine.Id + " annulee.", GameApi.Info);
                OpenUnpaidList(player);
            });
            panel.AddButton("Retour", ui => OpenUnpaidList(player));
            player.ShowPanelUI(panel);
        }

        // =====================================================================
        //  MENU INTERACTION (proprietaire)
        // =====================================================================
        private void OpenOwnerMenu(Player player)
        {
            List<Fine> mine = Store.ByOwner(player.character.Id);
            if (mine.Count == 0)
            {
                ShowMessage(player, "Mes contraventions", "Tu n'as aucune contravention a payer.", null);
                return;
            }

            UIPanel panel = new UIPanel("Mes contraventions", GameApi.Tab);
            foreach (Fine f in mine)
            {
                Fine fine = f;
                panel.AddTabLine("#" + fine.Id + " " + fine.Plate + " - <color=#FF5555>" + fine.Amount + " EUR</color>", ui => OpenPayPanel(player, fine));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        private void OpenPayPanel(Player player, Fine fine)
        {
            UIPanel panel = new UIPanel("Contravention #" + fine.Id, GameApi.Text);
            panel.text =
                "Vehicule : " + fine.Plate +
                "\nMotif : " + fine.Reason +
                "\nMontant : " + fine.Amount + " EUR" +
                "\nVerbalise par : " + fine.IssuerName +
                "\n\nTon vehicule restera bloque tant que tout n'est pas paye.";
            panel.AddButton("Payer " + fine.Amount + " EUR", ui =>
            {
                if (!Store.All.Contains(fine))
                {
                    GameApi.Notify(player, "Contravention", "Cette contravention n'existe plus.", GameApi.Info);
                    OpenOwnerMenu(player);
                    return;
                }
                if (fine.OwnerCharacterId != player.character.Id)
                    return;

                double money = GameApi.GetMoney(player);
                if (money >= 0 && money < fine.Amount)
                {
                    GameApi.Notify(player, "Contravention", "Pas assez d'argent sur toi.", GameApi.Error);
                    return;
                }
                if (!GameApi.RemoveMoney(player, fine.Amount, "Contravention #" + fine.Id))
                {
                    GameApi.Notify(player, "Contravention", "Paiement impossible (voir les logs du serveur).", GameApi.Error);
                    return;
                }

                Store.Remove(fine);
                bool stillBlocked = Store.IsBlocked(fine.VehicleDbId);
                GameApi.Notify(player, "Contravention",
                    "Contravention payee." + (stillBlocked ? " Il reste d'autres contraventions sur ce vehicule." : " Ton vehicule est debloque."),
                    GameApi.Success);
                OpenOwnerMenu(player);
            });
            panel.AddButton("Retour", ui => OpenOwnerMenu(player));
            player.ShowPanelUI(panel);
        }

        // =====================================================================
        //  Utilitaires
        // =====================================================================
        private static void ShowMessage(Player player, string title, string text, Action back)
        {
            UIPanel panel = new UIPanel(title, GameApi.Text);
            panel.text = text;
            if (back != null) panel.AddButton("Retour", ui => back());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        private static string SafeVehicleName(Vehicle veh)
        {
            try { return veh.VehicleName; }
            catch { return null; }
        }
    }

    // =========================================================================
    //  Donnees
    // =========================================================================
    public class ContraventionConfig
    {
        /// <summary>ID de l'entreprise autorisee a verbaliser (0 = personne).</summary>
        public int BizId = 0;
        /// <summary>Montant maximum d'une contravention.</summary>
        public int MaxAmount = 100000;
        /// <summary>Distance maximale (m) pour verbaliser un vehicule.</summary>
        public float VehicleRadius = 10f;

        public static ContraventionConfig Load(string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            ContraventionConfig config = null;
            try
            {
                if (File.Exists(path))
                    config = JsonConvert.DeserializeObject<ContraventionConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Logger.LogError("Contravention", "config.json invalide : " + ex.Message);
            }
            if (config == null) config = new ContraventionConfig();
            if (config.MaxAmount < 1) config.MaxAmount = 100000;
            if (config.VehicleRadius <= 0f) config.VehicleRadius = 10f;
            try { File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented)); }
            catch (Exception ex) { Logger.LogError("Contravention", "Ecriture config.json impossible : " + ex.Message); }
            return config;
        }
    }

    public class Fine
    {
        public int Id;
        public int VehicleDbId;
        public string Plate;
        public int OwnerCharacterId;
        public int Amount;
        public string Reason;
        public string IssuerName;
        public DateTime DateUtc;
    }

    public class FineStore
    {
        public int NextId = 1;
        public List<Fine> Fines = new List<Fine>();

        [JsonIgnore] public List<Fine> All { get { return Fines; } }
        [JsonIgnore] private string _path;
        [JsonIgnore] private HashSet<int> _blocked = new HashSet<int>();

        public static FineStore Load(string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "fines.json");
            FineStore store = null;
            try
            {
                if (File.Exists(path))
                    store = JsonConvert.DeserializeObject<FineStore>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Logger.LogError("Contravention", "fines.json invalide (copie dans fines.json.bak) : " + ex.Message);
                try { File.Copy(path, path + ".bak", true); } catch { }
            }
            if (store == null) store = new FineStore();
            if (store.Fines == null) store.Fines = new List<Fine>();
            store.Fines.RemoveAll(f => f == null);
            if (store.Fines.Count > 0) store.NextId = Math.Max(store.NextId, store.Fines.Max(f => f.Id) + 1);
            store._path = path;
            store.Rebuild();
            return store;
        }

        public Fine Add(Fine fine)
        {
            fine.Id = NextId++;
            Fines.Add(fine);
            Rebuild();
            Save();
            return fine;
        }

        public bool Remove(Fine fine)
        {
            bool removed = Fines.Remove(fine);
            if (removed)
            {
                Rebuild();
                Save();
            }
            return removed;
        }

        public List<Fine> ByOwner(int characterId)
        {
            return Fines.Where(f => f.OwnerCharacterId > 0 && f.OwnerCharacterId == characterId).OrderBy(f => f.Id).ToList();
        }

        public bool IsBlocked(int vehicleDbId)
        {
            return vehicleDbId > 0 && _blocked.Contains(vehicleDbId);
        }

        private void Rebuild()
        {
            _blocked = new HashSet<int>(Fines.Where(f => f.VehicleDbId > 0).Select(f => f.VehicleDbId));
        }

        private void Save()
        {
            try { File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented)); }
            catch (Exception ex) { Logger.LogError("Contravention", "Sauvegarde fines.json impossible : " + ex.Message); }
        }
    }

    // =========================================================================
    //  Blocage des vehicules : maintenu toutes les secondes
    // =========================================================================
    public class FineEnforcer : MonoBehaviour
    {
        private readonly HashSet<int> _locked = new HashSet<int>();
        // joueur (netId) -> vehicule dont il a deja ete sorti
        private readonly Dictionary<uint, int> _ejected = new Dictionary<uint, int>();

        private void Start()
        {
            InvokeRepeating("Tick", 2f, 1f);
        }

        private void Tick()
        {
            ContraventionPlugin plugin = ContraventionPlugin.Instance;
            if (plugin == null || plugin.Store == null) return;
            FineStore store = plugin.Store;
            if (store.All.Count == 0 && _locked.Count == 0 && _ejected.Count == 0) return;

            try
            {
                foreach (Vehicle veh in UnityEngine.Object.FindObjectsOfType<Vehicle>())
                {
                    if (veh == null) continue;
                    int id = veh.VehicleDbId;
                    if (store.IsBlocked(id))
                    {
                        VehicleApi.SetBlocked(veh, true);
                        _locked.Add(id);
                    }
                    else if (_locked.Remove(id))
                    {
                        VehicleApi.SetBlocked(veh, false);
                    }
                }

                // Sortir les joueurs d'un vehicule bloque (une fois par entree)
                foreach (Player player in Nova.server.Players.ToList())
                {
                    if (player == null || player.setup == null) continue;
                    Vehicle veh = null;
                    try
                    {
                        var driver = player.GetVehicle();
                        if (driver != null) veh = driver.vehicle;
                    }
                    catch { }

                    if (veh == null || !store.IsBlocked(veh.VehicleDbId))
                    {
                        _ejected.Remove(player.netId);
                        continue;
                    }

                    int already;
                    if (_ejected.TryGetValue(player.netId, out already) && already == veh.VehicleDbId) continue;
                    _ejected[player.netId] = veh.VehicleDbId;

                    player.setup.TargetSetPosition(veh.transform.position + veh.transform.right * 3f);
                    GameApi.Notify(player, "Contravention",
                        "Ce vehicule est bloque par une contravention impayee (menu AA > Interaction > Mes contraventions).",
                        GameApi.Warning);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }

    // =========================================================================
    //  Acces au jeu
    //  Les membres non utilises par ModKit (argent, plaque, proprietaire,
    //  verrou) sont lus par reflexion : le plugin ne plante pas si leur nom
    //  change. Au demarrage, les noms trouves sont ecrits dans les logs.
    // =========================================================================
    public static class GameApi
    {
        public static readonly UIPanel.PanelType Text = ParseEnum<UIPanel.PanelType>("Text");
        public static readonly UIPanel.PanelType Input = ParseEnum<UIPanel.PanelType>("Input");
        public static readonly UIPanel.PanelType Tab = ParseEnum<UIPanel.PanelType>("Tab");
        public static readonly NotificationManager.Type Info = ParseEnum<NotificationManager.Type>("Info");
        public static readonly NotificationManager.Type Success = ParseEnum<NotificationManager.Type>("Success");
        public static readonly NotificationManager.Type Warning = ParseEnum<NotificationManager.Type>("Warning");
        public static readonly NotificationManager.Type Error = ParseEnum<NotificationManager.Type>("Error");

        private static T ParseEnum<T>(string name)
        {
            try { return (T)Enum.Parse(typeof(T), name, true); }
            catch
            {
                Debug.Log("[Contravention] Valeur " + typeof(T).Name + "." + name + " introuvable, valeur par defaut utilisee.");
                return default(T);
            }
        }

        public static void Notify(Player player, string title, string message, NotificationManager.Type type)
        {
            if (player != null) player.Notify(title, message, type, 6f);
        }

        public static Player OnlineByCharacterId(int characterId)
        {
            return Nova.server.Players.FirstOrDefault(p => p != null && p.character != null && p.character.Id == characterId);
        }

        /// <summary>Argent liquide du joueur, ou -1 si illisible.</summary>
        public static double GetMoney(Player player)
        {
            object value = Reflect.Get(player.character, "Money", "money");
            if (value == null) value = Reflect.Get(player, "Money", "money");
            if (value == null) return -1;
            try { return Convert.ToDouble(value); }
            catch { return -1; }
        }

        public static bool RemoveMoney(Player player, int amount, string reason)
        {
            try
            {
                if (Reflect.CallMoney(player, "RemoveMoney", amount, reason)) return true;
                if (Reflect.CallMoney(player, "AddMoney", -amount, reason)) return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }
            Logger.LogError("Contravention", "Aucune methode AddMoney/RemoveMoney trouvee sur Player.");
            return false;
        }

        public static void LogDiagnostics()
        {
            Debug.Log("[Contravention] Player argent : " + Reflect.Describe(typeof(Player), "money"));
            Debug.Log("[Contravention] Characters argent : " + Reflect.Describe(typeof(Life.DB.Characters), "money"));
            Debug.Log("[Contravention] Vehicle plaque/proprio/verrou/moteur : " + Reflect.Describe(typeof(Vehicle), "plate", "perm", "owner", "lock", "engine"));
            Debug.Log("[Contravention] LifeVehicle plaque/proprio : " + Reflect.Describe(typeof(LifeVehicle), "plate", "perm", "owner"));
        }
    }

    public static class VehicleApi
    {
        private static readonly string[] LockNames = { "NetworkisLocked", "isLocked", "NetworkIsLocked", "IsLocked", "Networklocked", "locked" };
        private static readonly string[] EngineNames = { "NetworkisEngineStarted", "isEngineStarted", "NetworkengineStarted", "engineStarted", "NetworkisEngineOn", "isEngineOn", "NetworkengineOn", "engineOn" };

        public static string GetPlate(Vehicle veh)
        {
            if (veh == null) return null;
            object plate = Reflect.Get(veh, "Networkplate", "plate", "Plate", "licensePlate");
            if (plate == null)
            {
                LifeVehicle lv = GetLifeVehicle(veh);
                if (lv != null) plate = Reflect.Get(lv, "plate", "Plate");
            }
            string s = plate as string;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static int GetOwnerCharacterId(Vehicle veh)
        {
            LifeVehicle lv = GetLifeVehicle(veh);
            int id = OwnerFromPermissions(lv != null ? Reflect.Get(lv, "permissions", "Permissions") : null);
            if (id <= 0) id = OwnerFromPermissions(Reflect.Get(veh, "Networkpermissions", "permissions", "Permissions"));
            return id;
        }

        public static void SetBlocked(Vehicle veh, bool blocked)
        {
            if (veh == null) return;
            Reflect.SetBool(veh, blocked, LockNames);
            if (blocked) Reflect.SetBool(veh, false, EngineNames);
        }

        private static LifeVehicle GetLifeVehicle(Vehicle veh)
        {
            try { return veh != null && veh.VehicleDbId > 0 ? Nova.v.GetVehicle(veh.VehicleDbId) : null; }
            catch { return null; }
        }

        private static int OwnerFromPermissions(object permissions)
        {
            if (permissions == null) return 0;
            try
            {
                string json = permissions as string;
                if (json != null)
                {
                    JToken token = JObject.Parse(json).SelectToken("owner.characterId");
                    return token != null ? token.Value<int>() : 0;
                }
                object owner = Reflect.Get(permissions, "owner", "Owner");
                object id = owner != null ? Reflect.Get(owner, "characterId", "CharacterId") : null;
                return id != null ? Convert.ToInt32(id) : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    internal static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static object Get(object target, params string[] names)
        {
            if (target == null) return null;
            Type type = target.GetType();
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = type.GetProperty(name, Flags);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
                    FieldInfo f = type.GetField(name, Flags);
                    if (f != null) return f.GetValue(target);
                }
                catch { }
            }
            return null;
        }

        /// <summary>Met la valeur sur le premier membre bool trouve parmi les noms donnes.</summary>
        public static bool SetBool(object target, bool value, params string[] names)
        {
            if (target == null) return false;
            Type type = target.GetType();
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = type.GetProperty(name, Flags);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                    {
                        if (!Equals(p.GetValue(target, null), value)) p.SetValue(target, value, null);
                        return true;
                    }
                    FieldInfo f = type.GetField(name, Flags);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        if (!Equals(f.GetValue(target), value)) f.SetValue(target, value);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>Appelle player.{name}(montant, raison) quel que soit le type numerique du montant.</summary>
        public static bool CallMoney(object target, string name, int amount, string reason)
        {
            foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == name))
            {
                ParameterInfo[] ps = m.GetParameters();
                Type t = ps.Length > 0 ? ps[0].ParameterType : null;
                if (t != typeof(double) && t != typeof(float) && t != typeof(int) && t != typeof(long)) continue;
                if (ps.Length == 1)
                {
                    m.Invoke(target, new object[] { Convert.ChangeType(amount, t) });
                    return true;
                }
                if (ps.Length == 2 && ps[1].ParameterType == typeof(string))
                {
                    m.Invoke(target, new object[] { Convert.ChangeType(amount, t), reason });
                    return true;
                }
            }
            return false;
        }

        public static string Describe(Type type, params string[] keywords)
        {
            IEnumerable<string> members = type.GetMembers(Flags)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Method)
                .Where(m => keywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(m => m.Name)
                .Distinct();
            string list = string.Join(", ", members.ToArray());
            return list.Length == 0 ? "(aucun)" : list;
        }
    }
}
