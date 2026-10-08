using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.CheckpointSystem;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using Logger = ModKit.Internal.Logger;
using static ModKit.Helper.TextFormattingHelper;

namespace DrugsDealers
{
    /// <summary>
    /// Drugs Dealers By Loris Strange.
    /// Les joueurs revendent à des clients (PNJ acheteurs) les marchandises configurées par le staff,
    /// uniquement pendant les heures d'ouverture. Les prix changent régulièrement (15 min par défaut),
    /// chaque lieu de vente a son propre coefficient de prix, chaque client a une demande limitée
    /// et disparaît quand on lui a tout vendu. Les forces de l'ordre peuvent être alertées.
    /// </summary>
    public class DrugsDealersPlugin : ModKit.ModKit
    {
        public const string Title = "Drugs Dealers";

        /// <summary>Distance max (m) entre le joueur et le client pour pouvoir vendre.</summary>
        private const float SellDistance = 6f;

        private readonly System.Random rng = new System.Random();

        internal DealerSettings Settings = new DealerSettings();
        internal List<DealerItem> Items = new List<DealerItem>();
        internal List<DealerSpot> Spots = new List<DealerSpot>();

        /// <summary>Prix de base courant de chaque item (itemId → prix).</summary>
        private readonly Dictionary<int, double> prices = new Dictionary<int, double>();
        private DateTime nextPriceRefresh = DateTime.MinValue;

        /// <summary>Client (PNJ acheteur) de chaque lieu (spotId → client).</summary>
        private readonly Dictionary<int, Buyer> buyers = new Dictionary<int, Buyer>();
        /// <summary>Vente en cours de chaque joueur (netId → mission).</summary>
        private readonly Dictionary<uint, Mission> missions = new Dictionary<uint, Mission>();
        /// <summary>Délai avant de pouvoir redemander un client (characterId → date).</summary>
        private readonly Dictionary<int, DateTime> cooldowns = new Dictionary<int, DateTime>();

        public DrugsDealersPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<DealerSettings>();
            Orm.RegisterTable<DealerItem>();
            Orm.RegisterTable<DealerSpot>();

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Vendre ma marchandise", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player == null) return;
                    if (ui is Panel menuPanel) menuPanel.Close();
                    else player.ClosePanel(ui);
                    StartSelling(player);
                });
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, Title, ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player != null) OpenAdmin(player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /dealer (joueurs) et /drugsdealers (staff).");
            }

            RegisterCommands();
            _ = RunLoop();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "Drugs Dealers By Loris Strange initialisé");
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            foreach (Mission mission in missions.Values.Where(m => m.Player.conn == conn).ToList())
                EndMission(mission, null, NotificationManager.Type.Info, playerLeft: true);
            base.OnPlayerDisconnect(conn);
        }

        private void RegisterCommands()
        {
            new SChatCommand("/dealer", "Trouver un client pour vendre ta marchandise", "/dealer",
                (Action<Player, string[]>)((player, args) => StartSelling(player))).Register();

            new SChatCommand("/drugsdealers", new[] { "/dd" }, "Configurer Drugs Dealers (staff)", "/drugsdealers",
                (Action<Player, string[]>)((player, args) => OpenAdmin(player))).Register();
        }

        // ------------------------------------------------------------------
        //  Données / boucle
        // ------------------------------------------------------------------

        private async Task RunLoop()
        {
            // Laisse le temps à l'ORM de créer les tables
            await Task.Delay(3000);
            await ReloadData();

            while (true)
            {
                try { Tick(); }
                catch (Exception e) { Logger.LogError(Title, e.ToString()); }
                await Task.Delay(5000);
            }
        }

        internal async Task ReloadData()
        {
            try
            {
                DealerSettings settings = (await DealerSettings.QueryAll()).FirstOrDefault();
                if (settings == null)
                {
                    settings = new DealerSettings();
                    await settings.Save();
                    settings = (await DealerSettings.QueryAll()).FirstOrDefault() ?? settings;
                }
                Settings = settings;
                Items = await DealerItem.QueryAll();
                Spots = await DealerSpot.QueryAll();
                RefreshPrices(false);
            }
            catch (Exception e)
            {
                Logger.LogError(Title, "Chargement des données : " + e.Message);
            }
        }

        private void Tick()
        {
            DateTime now = DateTime.Now;

            if (now >= nextPriceRefresh)
                RefreshPrices(true);

            bool open = IsOpen(now);
            foreach (Mission mission in missions.Values.ToList())
            {
                if (!open)
                    EndMission(mission, "Il est trop tard, le client est rentré chez lui.", NotificationManager.Type.Warning);
                else if (now >= mission.ExpiresAt)
                    EndMission(mission, "Le client en a eu marre d'attendre, il est parti.", NotificationManager.Type.Warning);
            }
        }

        /// <summary>Tire un nouveau prix de base (entre min et max) pour chaque marchandise.</summary>
        internal void RefreshPrices(bool announce)
        {
            prices.Clear();
            foreach (DealerItem item in Items)
            {
                double min = Math.Min(item.MinPrice, item.MaxPrice);
                double max = Math.Max(item.MinPrice, item.MaxPrice);
                prices[item.ItemId] = Math.Round(min + rng.NextDouble() * (max - min), 2);
            }
            nextPriceRefresh = DateTime.Now.AddMinutes(Math.Max(1, Settings.PriceRefreshMinutes));

            if (announce)
                foreach (Mission mission in missions.Values)
                    mission.Player.Notify(Title, "Les prix du marché viennent de changer.", NotificationManager.Type.Info, 5f);
        }

        internal double GetBasePrice(int itemId) => prices.TryGetValue(itemId, out double price) ? price : 0;

        internal double GetPrice(int itemId, DealerSpot spot) => Math.Round(GetBasePrice(itemId) * spot.PricePercent / 100.0, 2);

        internal bool IsOpen(DateTime now)
        {
            int start = Settings.StartHour, end = Settings.EndHour, hour = now.Hour;
            if (start == end) return true;
            return start < end ? hour >= start && hour < end : hour >= start || hour < end;
        }

        private static bool IsPolice(Player player)
        {
            return player != null && player.serviceMetier && player.GetActivities().Contains(Activity.Type.LawEnforcement);
        }

        private static Vector3 PositionOf(Player player) => ((Component)player.setup).transform.position;

        internal static string ItemName(Player player, int itemId)
        {
            Item item = ItemUtils.GetItemById(itemId);
            return item != null ? player.NewTranslate("Items", item.itemName) : $"Objet #{itemId}";
        }

        internal static string Money(double amount) => amount.ToString("0.##", CultureInfo.InvariantCulture) + "€";

        private static int CountItem(Player player, int itemId)
        {
            return InventoryUtils.ReturnPlayerInventory(player).TryGetValue(itemId, out int quantity) ? quantity : 0;
        }

        /// <summary>Client actuel d'un lieu (null si aucun client n'a encore été généré ou s'il peut réapparaître).</summary>
        private Buyer GetBuyer(DealerSpot spot)
        {
            if (buyers.TryGetValue(spot.Id, out Buyer buyer) && buyer.GoneUntil.HasValue && DateTime.Now >= buyer.GoneUntil.Value)
            {
                buyers.Remove(spot.Id);
                buyer = null;
            }
            return buyer;
        }

        internal string SpotState(DealerSpot spot)
        {
            Buyer buyer = GetBuyer(spot);
            if (buyer == null) return "client disponible";
            if (buyer.GoneUntil.HasValue) return $"client parti ({Math.Ceiling((buyer.GoneUntil.Value - DateTime.Now).TotalMinutes)} min)";
            if (buyer.ReservedBy.HasValue) return $"vente en cours (reste {buyer.Remaining})";
            return $"client présent (reste {buyer.Remaining})";
        }

        // ------------------------------------------------------------------
        //  Côté joueur : trouver un client puis vendre
        // ------------------------------------------------------------------

        public void StartSelling(Player player)
        {
            if (player == null) return;
            DateTime now = DateTime.Now;

            if (missions.TryGetValue(player.netId, out Mission current))
            {
                if (Vector3.Distance(PositionOf(player), current.Spot.Position) <= SellDistance)
                {
                    OpenSellMenu(player, current);
                    return;
                }
                player.setup.TargetSetGPSTarget(current.Spot.Position);
                player.Notify(Title, "Un client t'attend déjà, le point est sur ton GPS.", NotificationManager.Type.Info, 6f);
                return;
            }

            if (Items.Count == 0 || Spots.Count == 0)
            {
                player.Notify(Title, "Personne ne cherche à acheter quoi que ce soit ici.", NotificationManager.Type.Error, 6f);
                return;
            }
            if (!IsOpen(now))
            {
                player.Notify(Title, $"Personne n'achète à cette heure-ci. Reviens entre {Settings.StartHour}h et {Settings.EndHour}h.", NotificationManager.Type.Warning, 6f);
                return;
            }
            if (IsPolice(player) && !Settings.AllowPoliceToSell)
            {
                player.Notify(Title, "Impossible en service.", NotificationManager.Type.Error, 6f);
                return;
            }
            if (cooldowns.TryGetValue(player.character.Id, out DateTime until) && now < until)
            {
                player.Notify(Title, $"Fais-toi discret, réessaie dans {Math.Ceiling((until - now).TotalSeconds)} s.", NotificationManager.Type.Warning, 6f);
                return;
            }
            if (!Items.Any(i => CountItem(player, i.ItemId) > 0))
            {
                player.Notify(Title, "Tu n'as aucune marchandise qui intéresse les clients.", NotificationManager.Type.Error, 6f);
                return;
            }
            if (Settings.MinPoliceInService > 0 && Nova.server.GetAllInGamePlayers().Count(IsPolice) < Settings.MinPoliceInService)
            {
                player.Notify(Title, "Le quartier est trop calme, personne n'achète pour le moment.", NotificationManager.Type.Warning, 6f);
                return;
            }

            List<DealerSpot> available = Spots.Where(s =>
            {
                Buyer b = GetBuyer(s);
                return b == null || (!b.GoneUntil.HasValue && !b.ReservedBy.HasValue);
            }).ToList();
            if (available.Count == 0)
            {
                player.Notify(Title, "Aucun client disponible pour le moment, repasse plus tard.", NotificationManager.Type.Warning, 6f);
                return;
            }

            DealerSpot spot = available[rng.Next(available.Count)];
            if (!buyers.TryGetValue(spot.Id, out Buyer buyer))
            {
                int min = Math.Max(1, Math.Min(spot.MinQuantity, spot.MaxQuantity));
                int max = Math.Max(min, Math.Max(spot.MinQuantity, spot.MaxQuantity));
                buyer = new Buyer { Remaining = rng.Next(min, max + 1) };
                buyers[spot.Id] = buyer;
            }
            buyer.ReservedBy = player.netId;

            Mission mission = new Mission
            {
                Player = player,
                Spot = spot,
                Buyer = buyer,
                ExpiresAt = now.AddMinutes(Math.Max(1, Settings.MissionTimeoutMinutes))
            };
            mission.Checkpoint = new NCheckpoint(player.netId, spot.Position, _ =>
            {
                if (missions.TryGetValue(player.netId, out Mission m) && m == mission)
                    OpenSellMenu(player, mission);
            });
            missions[player.netId] = mission;

            player.CreateCheckpoint(mission.Checkpoint);
            player.setup.TargetSetGPSTarget(spot.Position);
            player.Notify(Title, "Viens me vendre ta marchandise à ce point là ! Le point est sur ton GPS.", NotificationManager.Type.Info, 10f);
        }

        private void OpenSellMenu(Player player, Mission mission)
        {
            if (!missions.TryGetValue(player.netId, out Mission m) || m != mission) return;

            Panel panel = PanelHelper.Create($"{mission.Spot.Name} - le client veut encore {mission.Buyer.Remaining} article(s)",
                UIPanel.PanelType.TabPrice, player, () => OpenSellMenu(player, mission));

            bool hasAny = false;
            foreach (DealerItem item in Items)
            {
                int owned = CountItem(player, item.ItemId);
                if (owned <= 0) continue;
                hasAny = true;
                double price = GetPrice(item.ItemId, mission.Spot);
                string name = ItemName(player, item.ItemId);
                panel.AddTabLine($"{name} (x{owned})", $"{Money(price)} / u", ItemUtils.GetIconIdByItemId(item.ItemId), _ =>
                    AskQuantity(player, mission, item.ItemId));
            }

            if (hasAny)
                panel.NextButton("Vendre", () => panel.SelectTab());
            else
                panel.AddTabLine("Tu n'as rien qui m'intéresse...", _ => { });

            panel.CloseButtonWithAction(Color("Partir", Colors.Error), () =>
            {
                EndMission(mission, "Tu as laissé tomber le client.", NotificationManager.Type.Info);
                return Task.FromResult(true);
            });
            panel.CloseButton();
            panel.Display();
        }

        private void AskQuantity(Player player, Mission mission, int itemId)
        {
            string name = ItemName(player, itemId);
            double price = GetPrice(itemId, mission.Spot);
            int owned = CountItem(player, itemId);
            int max = Math.Min(owned, mission.Buyer.Remaining);

            Panel panel = PanelHelper.Create($"Vendre : {name}", UIPanel.PanelType.Input, player, () => AskQuantity(player, mission, itemId));
            panel.TextLines.Add($"Prix : {Color(Money(price), Colors.Success)} l'unité");
            panel.TextLines.Add($"Tu en as {owned}, le client en veut encore {mission.Buyer.Remaining}.");
            panel.SetInputPlaceholder($"1 - {max}");

            panel.PreviousButtonWithAction("Vendre", () =>
            {
                if (!int.TryParse(panel.inputText, out int quantity) || quantity <= 0)
                {
                    player.Notify(Title, "Quantité invalide.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }
                return Task.FromResult(Sell(player, mission, itemId, quantity, panel));
            });
            panel.PreviousButtonWithAction("Tout vendre", () =>
                Task.FromResult(Sell(player, mission, itemId, Math.Min(CountItem(player, itemId), mission.Buyer.Remaining), panel)));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <returns>true pour revenir au menu de vente, false pour rester (erreur) ou si le panel a été fermé.</returns>
        private bool Sell(Player player, Mission mission, int itemId, int quantity, Panel panel)
        {
            if (!missions.TryGetValue(player.netId, out Mission m) || m != mission)
            {
                player.Notify(Title, "Le client n'est plus là.", NotificationManager.Type.Error, 5f);
                panel.Close();
                return false;
            }
            if (!IsOpen(DateTime.Now))
            {
                player.Notify(Title, "Il est trop tard, le client ne veut plus rien.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (Vector3.Distance(PositionOf(player), mission.Spot.Position) > SellDistance)
            {
                player.Notify(Title, "Tu es trop loin du client.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (quantity <= 0 || quantity > mission.Buyer.Remaining)
            {
                player.Notify(Title, $"Le client n'en veut que {mission.Buyer.Remaining}.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (!InventoryUtils.CheckInventoryContainsItem(player, itemId, quantity))
            {
                player.Notify(Title, "Tu n'en as pas autant.", NotificationManager.Type.Error, 5f);
                return false;
            }

            double unitPrice = GetPrice(itemId, mission.Spot);
            if (!player.CanAddMoney(unitPrice * quantity))
            {
                player.Notify(Title, "Tu ne peux pas porter autant d'argent sur toi.", NotificationManager.Type.Error, 5f);
                return false;
            }

            int removed = InventoryUtils.RemoveFromInventory(player, itemId, quantity);
            if (removed <= 0)
            {
                player.Notify(Title, "Impossible de vendre cet objet.", NotificationManager.Type.Error, 5f);
                return false;
            }

            double total = Math.Round(unitPrice * removed, 2);
            player.AddMoney(total, Title);
            mission.Buyer.Remaining -= removed;
            player.Notify(Title, $"Tu as vendu {removed} {ItemName(player, itemId)} pour {Money(total)}.", NotificationManager.Type.Success, 6f);

            if (!mission.AlertRolled)
            {
                mission.AlertRolled = true;
                TryAlertPolice(player, mission.Spot);
            }

            if (mission.Buyer.Remaining <= 0)
            {
                mission.Buyer.GoneUntil = DateTime.Now.AddMinutes(Math.Max(0, Settings.BuyerRespawnMinutes));
                EndMission(mission, "Le client a tout ce qu'il lui faut, il disparaît dans la nature.", NotificationManager.Type.Success);
                panel.Close();
                return false;
            }
            return true;
        }

        private void TryAlertPolice(Player dealer, DealerSpot spot)
        {
            if (rng.Next(100) >= Settings.AlertChance) return;

            // Position approximative : la police ne sait pas exactement où a lieu le deal
            double angle = rng.NextDouble() * Math.PI * 2;
            double distance = rng.NextDouble() * Math.Max(0, Settings.AlertRadius);
            Vector3 approx = spot.Position + new Vector3((float)(Math.Cos(angle) * distance), 0f, (float)(Math.Sin(angle) * distance));

            foreach (Player cop in Nova.server.GetAllInGamePlayers().Where(IsPolice))
            {
                cop.Notify("Appel anonyme", $"Un riverain signale un trafic de drogue vers « {spot.Name} » !", NotificationManager.Type.Warning, 15f);
                if (Settings.PoliceGps)
                    cop.setup.TargetSetGPSTarget(approx);
            }

            if (Settings.WarnDealer)
                dealer.Notify(Title, "Un passant t'a vu... les flics risquent de débarquer !", NotificationManager.Type.Warning, 8f);
        }

        private void EndMission(Mission mission, string message, NotificationManager.Type type, bool playerLeft = false)
        {
            Player player = mission.Player;
            if (missions.TryGetValue(player.netId, out Mission m) && m == mission)
                missions.Remove(player.netId);

            if (mission.Buyer.ReservedBy == player.netId)
                mission.Buyer.ReservedBy = null;

            if (playerLeft) return;

            cooldowns[player.character.Id] = DateTime.Now.AddSeconds(Math.Max(0, Settings.SellCooldownSeconds));
            try
            {
                player.DestroyCheckpoint(mission.Checkpoint);
                player.setup.TargetDisableNavigation();
            }
            catch (Exception e)
            {
                Logger.LogError(Title, "Fin de vente : " + e.Message);
            }
            if (message != null)
                player.Notify(Title, message, type, 6f);
        }

        // ------------------------------------------------------------------
        //  Côté staff : configuration
        // ------------------------------------------------------------------

        private static bool CheckStaff(Player player)
        {
            if (player.IsAdmin && player.serviceAdmin) return true;
            player.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
            return false;
        }

        private static string YesNo(bool value) => value ? Color("oui", Colors.Success) : Color("non", Colors.Error);

        public void OpenAdmin(Player player)
        {
            if (!CheckStaff(player)) return;
            DealerSettings s = Settings;

            Panel panel = PanelHelper.Create($"{Title} - Configuration", UIPanel.PanelType.Tab, player, () => OpenAdmin(player));
            panel.AddTabLine($"Marchandises ({Items.Count})", _ => ItemsMenu(player));
            panel.AddTabLine($"Lieux de vente / clients ({Spots.Count})", _ => SpotsMenu(player));
            panel.AddTabLine($"Prix actuels (changent dans {Math.Max(0, Math.Ceiling((nextPriceRefresh - DateTime.Now).TotalMinutes))} min)", _ => PricesMenu(player));

            panel.AddTabLine($"Heure d'ouverture : {s.StartHour}h", _ =>
                EditNumber(player, "Heure d'ouverture (0-23)", s.StartHour, 0, 23, v => s.StartHour = (int)v));
            panel.AddTabLine($"Heure de fermeture : {s.EndHour}h", _ =>
                EditNumber(player, "Heure de fermeture (0-23)", s.EndHour, 0, 23, v => s.EndHour = (int)v));
            panel.AddTabLine($"Changement des prix : toutes les {s.PriceRefreshMinutes} min", _ =>
                EditNumber(player, "Changement des prix (minutes)", s.PriceRefreshMinutes, 1, 1440, v => { s.PriceRefreshMinutes = (int)v; nextPriceRefresh = DateTime.Now.AddMinutes(s.PriceRefreshMinutes); }));
            panel.AddTabLine($"Temps pour rejoindre le client : {s.MissionTimeoutMinutes} min", _ =>
                EditNumber(player, "Temps pour rejoindre le client (minutes)", s.MissionTimeoutMinutes, 1, 120, v => s.MissionTimeoutMinutes = (int)v));
            panel.AddTabLine($"Réapparition d'un client : {s.BuyerRespawnMinutes} min", _ =>
                EditNumber(player, "Réapparition d'un client (minutes)", s.BuyerRespawnMinutes, 0, 1440, v => s.BuyerRespawnMinutes = (int)v));
            panel.AddTabLine($"Délai entre deux clients (joueur) : {s.SellCooldownSeconds} s", _ =>
                EditNumber(player, "Délai entre deux clients (secondes)", s.SellCooldownSeconds, 0, 3600, v => s.SellCooldownSeconds = (int)v));

            panel.AddTabLine($"Alerte police : {s.AlertChance}% de chance par client", _ =>
                EditNumber(player, "Chance d'alerter la police (%)", s.AlertChance, 0, 100, v => s.AlertChance = (int)v));
            panel.AddTabLine($"Imprécision de l'alerte : {s.AlertRadius} m", _ =>
                EditNumber(player, "Imprécision de la position envoyée à la police (m)", s.AlertRadius, 0, 1000, v => s.AlertRadius = (int)v));
            panel.AddTabLine($"Point GPS envoyé à la police : {YesNo(s.PoliceGps)}", async _ => { s.PoliceGps = !s.PoliceGps; await s.Save(); panel.Refresh(); });
            panel.AddTabLine($"Prévenir le vendeur quand la police est alertée : {YesNo(s.WarnDealer)}", async _ => { s.WarnDealer = !s.WarnDealer; await s.Save(); panel.Refresh(); });
            panel.AddTabLine($"Policiers en service requis : {s.MinPoliceInService}", _ =>
                EditNumber(player, "Nombre de policiers en service requis", s.MinPoliceInService, 0, 100, v => s.MinPoliceInService = (int)v));
            panel.AddTabLine($"La police en service peut vendre : {YesNo(s.AllowPoliceToSell)}", async _ => { s.AllowPoliceToSell = !s.AllowPoliceToSell; await s.Save(); panel.Refresh(); });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Saisie générique d'un nombre, enregistré dans les paramètres.</summary>
        private void EditNumber(Player player, string label, double current, double min, double max, Action<double> apply, Func<Task<bool>> save = null)
        {
            Panel panel = PanelHelper.Create($"{Title} - {label}", UIPanel.PanelType.Input, player,
                () => EditNumber(player, label, current, min, max, apply, save));
            panel.TextLines.Add(label);
            panel.TextLines.Add($"Valeur actuelle : {current.ToString(CultureInfo.InvariantCulture)} (entre {min.ToString(CultureInfo.InvariantCulture)} et {max.ToString(CultureInfo.InvariantCulture)})");
            panel.SetInputPlaceholder(current.ToString(CultureInfo.InvariantCulture));

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string input = panel.inputText?.Trim().Replace(',', '.');
                if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < min || value > max)
                {
                    player.Notify(Title, $"Valeur invalide (entre {min} et {max}).", NotificationManager.Type.Error, 5f);
                    return false;
                }
                apply(value);
                bool ok = save != null ? await save() : await Settings.Save();
                player.Notify(Title, ok ? "Enregistré." : "Erreur lors de l'enregistrement.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 4f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditText(Player player, string label, string current, Func<string, Task<bool>> apply)
        {
            Panel panel = PanelHelper.Create($"{Title} - {label}", UIPanel.PanelType.Input, player, () => EditText(player, label, current, apply));
            panel.TextLines.Add(label);
            panel.SetInputPlaceholder(current ?? "");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string value = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    player.Notify(Title, "Le texte ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                bool ok = await apply(value);
                player.Notify(Title, ok ? "Enregistré." : "Erreur lors de l'enregistrement.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 4f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---- Marchandises ----

        private void ItemsMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Title} - Marchandises", UIPanel.PanelType.TabPrice, player, () => ItemsMenu(player));

            if (Items.Count == 0)
                panel.AddTabLine("Aucune marchandise configurée", _ => { });
            foreach (DealerItem item in Items)
            {
                panel.AddTabLine(ItemName(player, item.ItemId), $"{Money(item.MinPrice)} - {Money(item.MaxPrice)}",
                    ItemUtils.GetIconIdByItemId(item.ItemId), _ => EditItem(player, item));
            }

            if (Items.Count > 0)
                panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton("Ajouter (inventaire)", () => AddItemFromInventory(player));
            panel.NextButton("Ajouter (ID)", () => EditText(player, "ID de l'item à ajouter", "", async text =>
            {
                if (!int.TryParse(text, out int itemId) || ItemUtils.GetItemById(itemId) == null)
                {
                    player.Notify(Title, "Cet item n'existe pas.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                return await AddItem(player, itemId);
            }));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AddItemFromInventory(Player player)
        {
            Panel panel = PanelHelper.Create($"{Title} - Ajouter depuis l'inventaire", UIPanel.PanelType.TabPrice, player, () => AddItemFromInventory(player));
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            if (inventory.Count == 0)
                panel.AddTabLine("Votre inventaire est vide", _ => { });
            foreach (int itemId in inventory.Keys)
            {
                bool already = Items.Any(i => i.ItemId == itemId);
                panel.AddTabLine(ItemName(player, itemId), already ? "déjà ajouté" : $"ID {itemId}", ItemUtils.GetIconIdByItemId(itemId), async _ =>
                {
                    if (await AddItem(player, itemId)) panel.Refresh();
                });
            }
            if (inventory.Count > 0)
                panel.AddButton("Ajouter", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> AddItem(Player player, int itemId)
        {
            if (Items.Any(i => i.ItemId == itemId))
            {
                player.Notify(Title, "Cette marchandise est déjà configurée.", NotificationManager.Type.Warning, 5f);
                return false;
            }
            DealerItem item = new DealerItem { ItemId = itemId, MinPrice = 50, MaxPrice = 100 };
            if (!await item.Save())
            {
                player.Notify(Title, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                return false;
            }
            await ReloadData();
            player.Notify(Title, $"{ItemName(player, itemId)} ajouté (prix 50€ - 100€, modifiable dans Marchandises).", NotificationManager.Type.Success, 6f);
            return true;
        }

        private void EditItem(Player player, DealerItem item)
        {
            string name = ItemName(player, item.ItemId);
            Panel panel = PanelHelper.Create($"{Title} - {name}", UIPanel.PanelType.Tab, player, () => EditItem(player, item));

            Func<Task<bool>> save = async () => { bool ok = await item.Save(); await ReloadData(); return ok; };
            panel.AddTabLine($"Prix minimum : {Money(item.MinPrice)}", _ =>
                EditNumber(player, $"Prix minimum de {name}", item.MinPrice, 0, 1000000, v => item.MinPrice = v, save));
            panel.AddTabLine($"Prix maximum : {Money(item.MaxPrice)}", _ =>
                EditNumber(player, $"Prix maximum de {name}", item.MaxPrice, 0, 1000000, v => item.MaxPrice = v, save));
            panel.AddTabLine($"Prix actuel : {Money(GetBasePrice(item.ItemId))}", _ => { });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButtonWithAction(Color("Supprimer", Colors.Error), async () =>
            {
                bool ok = await item.Delete();
                await ReloadData();
                player.Notify(Title, ok ? $"{name} retiré des marchandises." : "Erreur lors de la suppression.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void PricesMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Title} - Prix actuels", UIPanel.PanelType.TabPrice, player, () => PricesMenu(player));
            if (Items.Count == 0)
                panel.AddTabLine("Aucune marchandise configurée", _ => { });
            foreach (DealerItem item in Items)
                panel.AddTabLine(ItemName(player, item.ItemId), Money(GetBasePrice(item.ItemId)), ItemUtils.GetIconIdByItemId(item.ItemId), _ => { });

            panel.AddButton("Nouveaux prix", _ =>
            {
                RefreshPrices(true);
                player.Notify(Title, "Nouveaux prix tirés.", NotificationManager.Type.Success, 4f);
                panel.Refresh();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---- Lieux de vente / clients ----

        private void SpotsMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Title} - Lieux de vente", UIPanel.PanelType.Tab, player, () => SpotsMenu(player));

            if (Spots.Count == 0)
                panel.AddTabLine("Aucun lieu de vente", _ => { });
            foreach (DealerSpot spot in Spots)
            {
                panel.AddTabLine($"#{spot.Id} {spot.Name} - prix {spot.PricePercent}% - {SpotState(spot)}", _ => EditSpot(player, spot));
            }

            if (Spots.Count > 0)
                panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton("Créer ici", () => EditText(player, "Nom du nouveau lieu de vente", "Ruelle sombre", async name =>
            {
                Vector3 pos = PositionOf(player);
                DealerSpot spot = new DealerSpot { Name = name, X = pos.x, Y = pos.y, Z = pos.z, PricePercent = 100, MinQuantity = 5, MaxQuantity = 20 };
                bool ok = await spot.Save();
                await ReloadData();
                return ok;
            }));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditSpot(Player player, DealerSpot spot)
        {
            Panel panel = PanelHelper.Create($"{Title} - Lieu #{spot.Id}", UIPanel.PanelType.Tab, player, () => EditSpot(player, spot));
            Func<Task<bool>> save = async () => { bool ok = await spot.Save(); await ReloadData(); return ok; };

            panel.AddTabLine($"Nom : {spot.Name}", _ => EditText(player, "Nom du lieu de vente", spot.Name, async name => { spot.Name = name; return await save(); }));
            panel.AddTabLine($"Prix : {spot.PricePercent}% du prix du marché", _ =>
                EditNumber(player, "Prix payé ici (% du prix du marché)", spot.PricePercent, 1, 1000, v => spot.PricePercent = (int)v, save));
            panel.AddTabLine($"Demande minimum du client : {spot.MinQuantity}", _ =>
                EditNumber(player, "Quantité minimum achetée par le client", spot.MinQuantity, 1, 10000, v => spot.MinQuantity = (int)v, save));
            panel.AddTabLine($"Demande maximum du client : {spot.MaxQuantity}", _ =>
                EditNumber(player, "Quantité maximum achetée par le client", spot.MaxQuantity, 1, 10000, v => spot.MaxQuantity = (int)v, save));
            panel.AddTabLine($"État : {SpotState(spot)}", _ => { });
            panel.AddTabLine("Se téléporter", _ => player.setup.TargetSetPosition(spot.Position));
            panel.AddTabLine("Déplacer ici", async _ =>
            {
                Vector3 pos = PositionOf(player);
                spot.X = pos.x; spot.Y = pos.y; spot.Z = pos.z;
                bool ok = await save();
                player.Notify(Title, ok ? "Lieu déplacé à votre position." : "Erreur lors de l'enregistrement.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 4f);
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButtonWithAction(Color("Supprimer", Colors.Error), async () =>
            {
                bool ok = await spot.Delete();
                buyers.Remove(spot.Id);
                await ReloadData();
                player.Notify(Title, ok ? $"Lieu « {spot.Name} » supprimé." : "Erreur lors de la suppression.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }

    // ----------------------------------------------------------------------
    //  État en mémoire
    // ----------------------------------------------------------------------

    /// <summary>Client (PNJ acheteur) d'un lieu de vente.</summary>
    internal class Buyer
    {
        /// <summary>Nombre d'articles que le client veut encore acheter.</summary>
        public int Remaining;
        /// <summary>Joueur qui est en train de lui vendre.</summary>
        public uint? ReservedBy;
        /// <summary>Le client est parti (tout acheté) jusqu'à cette date.</summary>
        public DateTime? GoneUntil;
    }

    /// <summary>Vente en cours d'un joueur.</summary>
    internal class Mission
    {
        public Player Player;
        public DealerSpot Spot;
        public Buyer Buyer;
        public NCheckpoint Checkpoint;
        public DateTime ExpiresAt;
        public bool AlertRolled;
    }

    // ----------------------------------------------------------------------
    //  Tables (base ModKit)
    // ----------------------------------------------------------------------

    /// <summary>Paramètres généraux (une seule ligne).</summary>
    public class DealerSettings : ModEntity<DealerSettings>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int StartHour { get; set; } = 22;
        public int EndHour { get; set; } = 5;
        public int PriceRefreshMinutes { get; set; } = 15;
        public int MissionTimeoutMinutes { get; set; } = 10;
        public int BuyerRespawnMinutes { get; set; } = 20;
        public int SellCooldownSeconds { get; set; } = 60;
        public int AlertChance { get; set; } = 30;
        public int AlertRadius { get; set; } = 50;
        public bool PoliceGps { get; set; } = true;
        public bool WarnDealer { get; set; } = false;
        public int MinPoliceInService { get; set; } = 0;
        public bool AllowPoliceToSell { get; set; } = false;
    }

    /// <summary>Marchandise achetée par les clients, avec sa fourchette de prix.</summary>
    public class DealerItem : ModEntity<DealerItem>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int ItemId { get; set; }
        public double MinPrice { get; set; }
        public double MaxPrice { get; set; }
    }

    /// <summary>Lieu où un client (PNJ acheteur) attend les vendeurs.</summary>
    public class DealerSpot : ModEntity<DealerSpot>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        /// <summary>Pourcentage du prix du marché payé à cet endroit (100 = prix normal).</summary>
        public int PricePercent { get; set; } = 100;
        public int MinQuantity { get; set; } = 5;
        public int MaxQuantity { get; set; } = 20;

        [Ignore]
        public Vector3 Position => new Vector3(X, Y, Z);
    }
}
