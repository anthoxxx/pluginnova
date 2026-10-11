using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Life;
using Life.BizSystem;
using Life.Network;
using Life.UI;
using Life.VehicleSystem;
using ModKit.Interfaces;
using Newtonsoft.Json;
using UnityEngine;
using _menu = AAMenu.Menu;

namespace Contravention
{
    public class ContraventionPlugin : ModKit.ModKit
    {
        public static ContraventionPlugin Instance;
        public ContraventionConfig Config;
        public FineStore Store;
        public FineEnforcer Enforcer;

        private const float VehicleRadius = 10f;   // distance max pour verbaliser une voiture (m)
        private const int MaxAmount = 100000;      // montant max d'une contravention

        public ContraventionPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations("Contravention", "1.0.0", "Alfred");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            string dir = Path.Combine(PluginPath, "Contravention");
            Config = ContraventionConfig.Load(dir);
            Store = FineStore.Load(dir);

            var go = new GameObject("ContraventionEnforcer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Enforcer = go.AddComponent<FineEnforcer>();

            // Menu AA metier (visible dans l'onglet biz, reserve au biz du JSON)
            var allActivities = Enum.GetValues(typeof(Activity.Type)).Cast<Activity.Type>().ToList();
            _menu.AddBizTabLine(PluginInformations, allActivities, null, "Contraventions", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                OpenOfficerMenu(player);
            });

            // Menu AA interaction (tous les joueurs : payer ses contraventions)
            _menu.AddInteractionTabLine(PluginInformations, "Mes contraventions", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                OpenOwnerMenu(player);
            });

            Debug.Log("[Contravention] Plugin charge. BizId=" + Config.BizId + " | contraventions impayees=" + Store.All.Count);
            if (Config.BizId <= 0)
                Debug.Log("[Contravention] BizId non configure : renseigner l'id de l'entreprise dans " + Path.Combine(dir, "config.json") + " puis redemarrer.");
        }

        private bool IsOfficer(Player p) => Config.BizId > 0 && PlayerApi.BizId(p) == Config.BizId;

        // =====================================================================
        //  MENU METIER
        // =====================================================================
        private void OpenOfficerMenu(Player player)
        {
            if (!IsOfficer(player))
            {
                PlayerApi.Notify(player, "Contraventions", "Tu n'appartiens pas a ce metier.", NotificationManager.Type.Error);
                return;
            }

            var panel = new UIPanel("Contraventions", UIPanel.PanelType.Tab);
            panel.AddTabLine("Donner une contravention", ui => OpenVehiclePicker(player));
            panel.AddTabLine("Contraventions impayees (" + Store.All.Count + ")", ui => OpenUnpaidList(player));
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        private void OpenVehiclePicker(Player player)
        {
            var vehicles = VehicleAdapter.Nearby(player, VehicleRadius);

            if (vehicles.Count == 0)
            {
                var empty = new UIPanel("Vehicule a verbaliser", UIPanel.PanelType.Text);
                empty.text = "Aucun vehicule a moins de " + (int)VehicleRadius + " m.";
                empty.AddButton("Retour", ui => OpenOfficerMenu(player));
                player.ShowPanelUI(empty);
                return;
            }

            var panel = new UIPanel("Vehicule a verbaliser", UIPanel.PanelType.Tab);
            var pos = PlayerApi.Position(player);
            foreach (var v in vehicles)
            {
                var veh = v;
                string plate = VehicleAdapter.GetPlate(veh) ?? "?";
                int dist = (int)Vector3.Distance(veh.transform.position, pos);
                panel.AddTabLine(plate + "  <color=#AAAAAA>(" + dist + " m)</color>", ui => AskAmount(player, veh));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => OpenOfficerMenu(player));
            player.ShowPanelUI(panel);
        }

        private void AskAmount(Player player, Vehicle veh)
        {
            var panel = new UIPanel("Montant (EUR)", UIPanel.PanelType.Input);
            panel.text = "Plaque : " + (VehicleAdapter.GetPlate(veh) ?? "?") + "\nMontant de la contravention (1 - " + MaxAmount + ")";
            panel.inputPlaceholder = "Ex : 500";
            panel.AddButton("Suivant", ui =>
            {
                if (!int.TryParse((ui.inputText ?? "").Trim(), out int amount) || amount < 1 || amount > MaxAmount)
                {
                    PlayerApi.Notify(player, "Contraventions", "Montant invalide (1 - " + MaxAmount + ").", NotificationManager.Type.Error);
                    return;
                }
                AskReason(player, veh, amount);
            });
            panel.AddButton("Retour", ui => OpenVehiclePicker(player));
            player.ShowPanelUI(panel);
        }

        private void AskReason(Player player, Vehicle veh, int amount)
        {
            var panel = new UIPanel("Motif", UIPanel.PanelType.Input);
            panel.text = "Motif de la contravention de " + amount + " EUR";
            panel.inputPlaceholder = "Ex : Stationnement genant";
            panel.AddButton("Verbaliser", ui =>
            {
                string reason = (ui.inputText ?? "").Trim();
                if (reason.Length < 3)
                {
                    PlayerApi.Notify(player, "Contraventions", "Motif trop court.", NotificationManager.Type.Error);
                    return;
                }
                IssueFine(player, veh, amount, reason);
                player.ClosePanel(ui);
            });
            panel.AddButton("Retour", ui => AskAmount(player, veh));
            player.ShowPanelUI(panel);
        }

        private void IssueFine(Player officer, Vehicle veh, int amount, string reason)
        {
            if (veh == null)
            {
                PlayerApi.Notify(officer, "Contraventions", "Vehicule introuvable.", NotificationManager.Type.Error);
                return;
            }

            int dbId = VehicleAdapter.GetDbId(veh);
            string plate = VehicleAdapter.GetPlate(veh) ?? "?";
            int ownerId = VehicleAdapter.GetOwnerCharacterId(veh);

            var fine = Store.Add(new Fine
            {
                VehicleDbId = dbId,
                Plate = plate,
                OwnerCharacterId = ownerId,
                Amount = amount,
                Reason = reason,
                IssuerName = PlayerApi.FullName(officer)
            });

            bool locked = VehicleAdapter.SetLocked(veh, true);
            Enforcer?.Tick(); // blocage immediat

            PlayerApi.Notify(officer, "Contraventions",
                "Contravention #" + fine.Id + " de " + amount + " EUR donnee a " + plate + "." + (locked ? " Vehicule bloque." : ""),
                NotificationManager.Type.Success);

            var owner = ownerId > 0 ? PlayerApi.OnlineByCharacterId(ownerId) : null;
            if (owner != null)
                PlayerApi.Notify(owner, "Contravention",
                    "Ton vehicule " + plate + " a recu une contravention de " + amount + " EUR (" + reason +
                    "). Il est bloque jusqu'au paiement : menu AA > Interaction > Mes contraventions.",
                    NotificationManager.Type.Warning);
        }

        private void OpenUnpaidList(Player player)
        {
            if (Store.All.Count == 0)
            {
                var empty = new UIPanel("Contraventions impayees", UIPanel.PanelType.Text);
                empty.text = "Aucune contravention impayee.";
                empty.AddButton("Retour", ui => OpenOfficerMenu(player));
                player.ShowPanelUI(empty);
                return;
            }

            var panel = new UIPanel("Contraventions impayees", UIPanel.PanelType.Tab);
            foreach (var f in Store.All.OrderByDescending(x => x.DateUtc).Take(60))
            {
                var fine = f;
                panel.AddTabLine("#" + fine.Id + " " + fine.Plate + " - " + fine.Amount + " EUR", ui => OpenOfficerFineDetail(player, fine));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => OpenOfficerMenu(player));
            player.ShowPanelUI(panel);
        }

        private void OpenOfficerFineDetail(Player player, Fine fine)
        {
            var panel = new UIPanel("Contravention #" + fine.Id, UIPanel.PanelType.Text);
            panel.text =
                "Plaque : " + fine.Plate +
                "\nMontant : " + fine.Amount + " EUR" +
                "\nMotif : " + fine.Reason +
                "\nVerbalise par : " + fine.IssuerName +
                "\nDate : " + fine.DateUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            panel.AddButton("Annuler la contravention", ui =>
            {
                Store.Remove(fine);
                Enforcer?.Tick(); // deblocage immediat si plus aucune amende
                PlayerApi.Notify(player, "Contraventions", "Contravention #" + fine.Id + " annulee.", NotificationManager.Type.Info);
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
            int charId = PlayerApi.CharacterId(player);
            var mine = Store.ByOwner(charId);

            // Contraventions des vehicules a proximite (si le proprietaire n'a pas pu etre lu)
            foreach (var veh in VehicleAdapter.Nearby(player, VehicleRadius))
                foreach (var f in Store.ForVehicle(VehicleAdapter.GetDbId(veh), VehicleAdapter.GetPlate(veh)))
                    if (!mine.Contains(f)) mine.Add(f);

            if (mine.Count == 0)
            {
                var empty = new UIPanel("Mes contraventions", UIPanel.PanelType.Text);
                empty.text = "Tu n'as aucune contravention a payer.";
                empty.AddButton("Fermer", ui => player.ClosePanel(ui));
                player.ShowPanelUI(empty);
                return;
            }

            var panel = new UIPanel("Mes contraventions", UIPanel.PanelType.Tab);
            foreach (var f in mine)
            {
                var fine = f;
                panel.AddTabLine("#" + fine.Id + " " + fine.Plate + " - <color=#FF5555>" + fine.Amount + " EUR</color>", ui => OpenPayPanel(player, fine));
            }
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        private void OpenPayPanel(Player player, Fine fine)
        {
            var panel = new UIPanel("Contravention #" + fine.Id, UIPanel.PanelType.Text);
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
                    PlayerApi.Notify(player, "Contravention", "Cette contravention n'existe plus.", NotificationManager.Type.Info);
                    OpenOwnerMenu(player);
                    return;
                }
                double cash = PlayerApi.Money(player);
                if (cash < fine.Amount)
                {
                    PlayerApi.Notify(player, "Contravention", "Pas assez d'argent sur toi (" + Math.Floor(cash) + " EUR).", NotificationManager.Type.Error);
                    return;
                }
                if (!PlayerApi.RemoveMoney(player, fine.Amount, "Contravention #" + fine.Id))
                {
                    PlayerApi.Notify(player, "Contravention", "Paiement impossible, previens un admin.", NotificationManager.Type.Error);
                    return;
                }

                Store.Remove(fine);
                Enforcer?.Tick(); // deblocage immediat

                bool stillBlocked = Store.IsBlocked(fine.VehicleDbId, fine.Plate);
                PlayerApi.Notify(player, "Contravention",
                    "Contravention payee." + (stillBlocked ? " Il reste d'autres contraventions sur ce vehicule." : " Ton vehicule est debloque."),
                    NotificationManager.Type.Success);
                OpenOwnerMenu(player);
            });
            panel.AddButton("Retour", ui => OpenOwnerMenu(player));
            player.ShowPanelUI(panel);
        }
    }

    // =========================================================================
    //  DONNEES
    // =========================================================================
    public class ContraventionConfig
    {
        /// <summary>Id de l'entreprise (biz) autorisee a verbaliser.</summary>
        public int BizId = 0;

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
                Debug.Log("[Contravention] config.json invalide : " + ex.Message);
            }
            config = config ?? new ContraventionConfig();
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
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
        public DateTime DateUtc = DateTime.UtcNow;

        public bool Matches(int vehicleDbId, string plate)
        {
            if (VehicleDbId > 0 && vehicleDbId > 0) return VehicleDbId == vehicleDbId;
            return !string.IsNullOrEmpty(plate) && plate != "?" && string.Equals(Plate, plate, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class FineStore
    {
        public int NextId = 1;
        public List<Fine> Fines = new List<Fine>();

        [JsonIgnore] private string _path;
        [JsonIgnore] public List<Fine> All => Fines;

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
                Debug.Log("[Contravention] fines.json invalide : " + ex.Message);
                File.Copy(path, path + ".bak", true);
            }
            store = store ?? new FineStore();
            store.Fines = store.Fines ?? new List<Fine>();
            store._path = path;
            store.Save();
            return store;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.Log("[Contravention] Sauvegarde impossible : " + ex.Message);
            }
        }

        public Fine Add(Fine fine)
        {
            fine.Id = NextId++;
            Fines.Add(fine);
            Save();
            return fine;
        }

        public void Remove(Fine fine)
        {
            if (Fines.Remove(fine)) Save();
        }

        public List<Fine> ByOwner(int characterId) =>
            characterId > 0 ? Fines.Where(f => f.OwnerCharacterId == characterId).ToList() : new List<Fine>();

        public List<Fine> ForVehicle(int vehicleDbId, string plate) =>
            Fines.Where(f => f.Matches(vehicleDbId, plate)).ToList();

        public bool IsBlocked(int vehicleDbId, string plate) => Fines.Any(f => f.Matches(vehicleDbId, plate));
    }

    // =========================================================================
    //  BLOCAGE DES VEHICULES
    // =========================================================================
    /// <summary>Verrouille regulierement les vehicules qui ont une contravention impayee.</summary>
    public class FineEnforcer : MonoBehaviour
    {
        private const float Interval = 3f;
        private float _next;
        private readonly HashSet<int> _lockedByUs = new HashSet<int>();

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Interval;
            Tick();
        }

        public void Tick()
        {
            var store = ContraventionPlugin.Instance?.Store;
            if (store == null) return;
            try
            {
                foreach (var veh in UnityEngine.Object.FindObjectsOfType<Vehicle>())
                {
                    if (veh == null) continue;
                    int dbId = VehicleAdapter.GetDbId(veh);
                    if (dbId <= 0) continue;

                    if (store.IsBlocked(dbId, VehicleAdapter.GetPlate(veh)))
                    {
                        if (VehicleAdapter.IsLocked(veh) != true) VehicleAdapter.SetLocked(veh, true);
                        _lockedByUs.Add(dbId);
                    }
                    else if (_lockedByUs.Remove(dbId))
                    {
                        VehicleAdapter.SetLocked(veh, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[Contravention] Erreur blocage : " + ex);
            }
        }
    }

    // =========================================================================
    //  ACCES AU JEU
    // =========================================================================
    public static class PlayerApi
    {
        public static void Notify(Player p, string title, string message, NotificationManager.Type type)
        {
            if (p != null) p.Notify(title, message, type, 6f);
        }

        public static string FullName(Player p) => p?.FullName ?? "?";

        public static int CharacterId(Player p) => p?.character != null ? p.character.Id : 0;

        public static int BizId(Player p) => p != null && p.HasBiz && p.biz != null ? p.biz.Id : 0;

        public static Vector3 Position(Player p) => p.setup.transform.position;

        public static Player OnlineByCharacterId(int characterId) =>
            Nova.server.Players.FirstOrDefault(p => p != null && p.character != null && p.character.Id == characterId);

        /// <summary>Argent liquide du joueur.</summary>
        public static double Money(Player p) => p.Money;

        public static bool RemoveMoney(Player p, int amount, string reason)
        {
            double before = p.Money;
            p.AddMoney(-amount, reason);
            return p.Money <= before - amount + 0.01;
        }
    }

    public static class VehicleAdapter
    {
        public static List<Vehicle> Nearby(Player player, float radius)
        {
            var pos = PlayerApi.Position(player);
            return UnityEngine.Object.FindObjectsOfType<Vehicle>()
                .Where(v => v != null && GetDbId(v) > 0 && Vector3.Distance(v.transform.position, pos) <= radius)
                .OrderBy(v => Vector3.Distance(v.transform.position, pos))
                .ToList();
        }

        public static int GetDbId(Vehicle veh) => veh != null ? veh.VehicleDbId : 0;

        public static LifeVehicle GetLifeVehicle(Vehicle veh)
        {
            int id = GetDbId(veh);
            return id > 0 ? Nova.v.GetVehicle(id) : null;
        }

        public static string GetPlate(Vehicle veh)
        {
            if (veh == null) return null;
            var plate = GetLifeVehicle(veh)?.plate;
            return string.IsNullOrWhiteSpace(plate) ? "#" + GetDbId(veh) : plate;
        }

        public static int GetOwnerCharacterId(Vehicle veh) => GetLifeVehicle(veh)?.permissions?.owner?.characterId ?? 0;

        public static bool? IsLocked(Vehicle veh) => veh != null ? veh.isLocked : (bool?)null;

        public static bool SetLocked(Vehicle veh, bool locked)
        {
            if (veh == null) return false;
            if (veh.isLocked != locked)
            {
                veh.NetworkisLocked = locked;
                veh.RpcOnLockVehicle();
            }
            return true;
        }
    }
}
