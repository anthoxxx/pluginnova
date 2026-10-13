using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Life.VehicleSystem;
using Mirror;
using ModKit.Helper;
using ModKit.Interfaces;
using Logger = ModKit.Internal.Logger;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace Gaz
{
    /// <summary>
    /// Gaz By Loris Strange : les pompes à essence du jeu sont bloquées, seules les
    /// entreprises autorisées peuvent servir de l'essence et facturer les clients.
    /// </summary>
    public class GazPlugin : ModKit.ModKit
    {
        public const string Title = "Gaz By Loris Strange";

        /// <summary>Distance max entre l'employé et la pompe pour qu'il puisse s'en servir.</summary>
        private const float EmployeeRange = 4f;
        /// <summary>Distance max pour lier une pompe configurée à une pompe du jeu.</summary>
        private const float LinkRange = 1.5f;
        /// <summary>Distance max pour choisir un client à facturer.</summary>
        private const float ClientRange = 10f;
        /// <summary>Pompes plus proches que ça = même station (détection automatique).</summary>
        private const float StationClusterRange = 30f;
        /// <summary>Jerricans remplis à la pompe par le jeu : plein -> vide.</summary>
        private static readonly Dictionary<int, int> Jerrycans = new Dictionary<int, int> { { 1564, 1183 }, { 1591, 1590 } };
        private const int JerrycanLiters = 15;

        public GazConfig Config { get; private set; }
        public List<GazPump> Pumps { get; } = new List<GazPump>();

        private bool _loaded;
        private bool _menuRegistered;
        private Coroutine _loop;

        private List<GasStation> _stations = new List<GasStation>();
        private readonly Dictionary<uint, GazPump> _links = new Dictionary<uint, GazPump>();
        private float _nextScan;

        private readonly Dictionary<(uint station, int slot), FillSession> _sessions = new Dictionary<(uint, int), FillSession>();
        private readonly Dictionary<int, List<PendingBill>> _bills = new Dictionary<int, List<PendingBill>>();
        private readonly Dictionary<uint, float> _lastBlockNotify = new Dictionary<uint, float>();

        public GazPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            _ = LoadAsync();
            TryRegisterMenu();
            RegisterCommands();
            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            Nova.server.OnPlayerReceiveItemEvent += OnPlayerReceiveItem;
            Logger.LogSuccess($"{Title} v{PluginInformations.Version}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            TryRegisterMenu();
            EnsureLoop();
        }

        private void OnMinutePassed()
        {
            TryRegisterMenu();
            EnsureLoop();
        }

        private async Task LoadAsync()
        {
            try
            {
                await Orm.SqliteConnection.CreateTableAsync<GazConfig>();
                await Orm.SqliteConnection.CreateTableAsync<GazPump>();
                await Orm.SqliteConnection.CreateTableAsync<GazSale>();

                Config = (await GazConfig.QueryAll())?.FirstOrDefault();
                if (Config == null)
                {
                    Config = new GazConfig();
                    await Config.Save();
                }

                Pumps.Clear();
                Pumps.AddRange(await GazPump.QueryAll() ?? new List<GazPump>());
                _loaded = true;
                _nextScan = 0f;
                EnsureLoop();
                Logger.LogSuccess(Title, $"{Pumps.Count} pompe(s) chargée(s)");
            }
            catch (Exception ex)
            {
                Logger.LogError(Title, "Chargement impossible : " + ex);
            }
        }

        private void TryRegisterMenu()
        {
            if (_menuRegistered || AAMenu.AAMenu.menu == null) return;
            _menuRegistered = true;

            // Activity.Type.None = visible par toutes les entreprises, le filtre est fait dans le menu
            AAMenu.Menu.AddBizTabLine(PluginInformations, new List<Activity.Type> { Activity.Type.None }, null,
                "Station-service", ui => EmployeeMenu(PanelHelper.ReturnPlayerFromPanel(ui)));
            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, Title,
                ui => AdminMenu(PanelHelper.ReturnPlayerFromPanel(ui)));
        }

        private void RegisterCommands()
        {
            // Secours si AAMenu n'est pas installé
            new SChatCommand("/gaz", "Station-service (" + Title + ")", "/gaz",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (player.IsAdmin && player.serviceAdmin) AdminMenu(player);
                    else EmployeeMenu(player);
                })).Register();
        }

        // ==================================================================
        //  Surveillance des pompes du jeu
        // ==================================================================

        private void EnsureLoop()
        {
            if (_loop != null || !_loaded || Nova.man == null) return;
            _loop = Nova.man.StartCoroutine(Loop());
        }

        private IEnumerator Loop()
        {
            WaitForSeconds wait = new WaitForSeconds(0.25f);
            while (true)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Logger.LogError(Title, "Tick : " + ex);
                }
                yield return wait;
            }
        }

        private void Tick()
        {
            if (Time.realtimeSinceStartup >= _nextScan)
            {
                _nextScan = Time.realtimeSinceStartup + 30f;
                _stations = UnityEngine.Object.FindObjectsOfType<GasStation>().ToList();
                RebuildLinks();
                ApplyGamePrice();
            }

            foreach (GasStation station in _stations)
            {
                if (station == null) continue;
                CheckSlot(station, 1);
                CheckSlot(station, 2);
            }
        }

        /// <summary>
        /// Le jeu facture lui-même l'essence au joueur qui raccroche le pistolet. On met ce prix
        /// à 0 pour que seul le plugin facture, et on affiche notre prix sur les panneaux des stations.
        /// </summary>
        private void ApplyGamePrice()
        {
            if (Nova.server.config?.roleplayConfig != null && Nova.server.config.roleplayConfig.fuelPrice != 0f)
                Nova.server.config.roleplayConfig.fuelPrice = 0f;
            if (ConfigManager.instance != null && Config != null)
            {
                float display = (float)Config.PrixLitre;
                if (!Mathf.Approximately(ConfigManager.instance.fuelPrice, display))
                    ConfigManager.instance.NetworkfuelPrice = display;
            }
        }

        public void RebuildLinks()
        {
            _links.Clear();
            foreach (GasStation station in _stations)
            {
                if (station == null) continue;
                Vector3 pos = station.transform.position;
                GazPump pump = Pumps
                    .Where(p => Vector3.Distance(p.Position, pos) <= LinkRange)
                    .OrderBy(p => Vector3.Distance(p.Position, pos))
                    .FirstOrDefault();
                if (pump != null) _links[station.netId] = pump;
            }
        }

        private void CheckSlot(GasStation station, int slot)
        {
            uint attached = slot == 1 ? station.pistolAttached1 : station.pistolAttached2;
            bool onVehicle = attached != 0 && attached != uint.MaxValue;
            var key = (station.netId, slot);

            if (_sessions.TryGetValue(key, out FillSession session) && (!onVehicle || attached != session.VehicleNetId))
            {
                _sessions.Remove(key);
                EndSession(session);
                session = null;
            }
            if (!onVehicle) return;

            Vehicle vehicle = NetworkServer.spawned.TryGetValue(attached, out NetworkIdentity identity) ? identity.GetComponent<Vehicle>() : null;
            int delivered = slot == 1 ? station.literDelivered1 : station.literDelivered2;

            if (session == null)
            {
                _links.TryGetValue(station.netId, out GazPump pump);
                Player employee = FindEmployee(station, pump, out string reason);
                if (employee == null)
                {
                    Block(station, slot, vehicle, delivered, reason);
                    return;
                }

                session = new FillSession
                {
                    Pump = pump,
                    EmployeeCharacterId = employee.character.Id,
                    BizId = employee.character.BizId,
                    VehicleNetId = attached,
                    IsElectric = station.isElectric,
                };
                _sessions[key] = session;
                employee.Notify("Station-service", $"Remplissage démarré ({pump.Label}) - {Money(PriceOf(pump))}/L", NotificationManager.Type.Info, 5f);
            }

            if (session.IsElectric)
            {
                // Les bornes électriques facturent elles-mêmes literDelivered : on le vide au fur et à mesure.
                if (delivered > 0)
                {
                    session.Liters += delivered;
                    SetDelivered(station, slot, 0);
                }
            }
            else
            {
                session.Liters = Math.Max(session.Liters, delivered);
            }

            if (Config.StockActif && session.Liters >= session.Pump.Stock)
            {
                session.Liters = Math.Min(session.Liters, Math.Max(session.Pump.Stock, 0));
                Detach(station, slot, vehicle);
                _sessions.Remove(key);
                PlayerByCharacter(session.EmployeeCharacterId)?.Notify("Station-service",
                    $"La citerne de {session.Pump.Label} est vide.", NotificationManager.Type.Warning, 6f);
                EndSession(session);
            }
        }

        /// <summary>Employé autorisé le plus proche de la pompe, ou null avec la raison du refus.</summary>
        private Player FindEmployee(GasStation station, GazPump pump, out string reason)
        {
            reason = null;
            if (pump == null)
            {
                reason = "Cette pompe est hors service.";
                return null;
            }
            if (!pump.Ouverte)
            {
                reason = $"{pump.Label} est fermée.";
                return null;
            }
            if (Config.StockActif && pump.Stock <= 0)
            {
                reason = $"La citerne de {pump.Label} est vide.";
                return null;
            }

            Vector3 pos = station.transform.position;
            Player employee = PlayersNear(pos, EmployeeRange).FirstOrDefault(p => IsAllowedAt(p, pump));
            if (employee == null)
                reason = $"Seul le personnel de {AllowedBizNames(pump)} peut servir de l'essence ici.";
            return employee;
        }

        private void Block(GasStation station, int slot, Vehicle vehicle, int delivered, string reason)
        {
            // Retire l'essence éventuellement passée avant le blocage
            if (vehicle != null && delivered > 0 && !station.isElectric)
                vehicle.Networkfuel = Mathf.Max(0f, vehicle.fuel - delivered);
            Detach(station, slot, vehicle);

            float now = Time.realtimeSinceStartup;
            foreach (Player player in PlayersNear(station.transform.position, 6f))
            {
                if (_lastBlockNotify.TryGetValue(player.netId, out float last) && now - last < 5f) continue;
                _lastBlockNotify[player.netId] = now;
                player.Notify("Station-service", reason, NotificationManager.Type.Error, 6f);
            }
        }

        private static void Detach(GasStation station, int slot, Vehicle vehicle)
        {
            if (slot == 1) station.NetworkpistolAttached1 = 0;
            else station.NetworkpistolAttached2 = 0;
            SetDelivered(station, slot, 0);
            if (vehicle != null && vehicle.gasStation != 0)
                vehicle.NetworkgasStation = 0;
        }

        private static void SetDelivered(GasStation station, int slot, int value)
        {
            if (slot == 1) station.NetworkliterDelivered1 = value;
            else station.NetworkliterDelivered2 = value;
        }

        private void EndSession(FillSession session)
        {
            if (session.Liters <= 0) return;

            GazPump pump = session.Pump;
            if (Config.StockActif)
            {
                pump.Stock = Math.Max(0, pump.Stock - session.Liters);
                _ = pump.Save();
            }

            PendingBill bill = AddPendingBill(session.EmployeeCharacterId, session.BizId, pump, session.Liters);

            Player employee = PlayerByCharacter(session.EmployeeCharacterId);
            if (employee != null)
            {
                employee.Notify("Station-service", $"{bill.Liters} L servis ({Money(bill.Total)}). Choisissez le client à facturer.", NotificationManager.Type.Success, 6f);
                BillMenu(employee, bill);
            }
        }

        private PendingBill AddPendingBill(int employeeId, int bizId, GazPump pump, int liters)
        {
            PendingBill bill = new PendingBill
            {
                Pump = pump,
                BizId = bizId,
                Liters = liters,
                PricePerLiter = PriceOf(pump),
            };
            if (!_bills.TryGetValue(employeeId, out List<PendingBill> list))
                _bills[employeeId] = list = new List<PendingBill>();
            list.Add(bill);
            return bill;
        }

        // ------------------------------------------------------------------
        //  Jerricans : le jeu les remplit à la pompe sans passer par le pistolet
        // ------------------------------------------------------------------

        private void OnPlayerReceiveItem(Player player, int itemId, int slotId, int number)
        {
            if (!_loaded || player?.setup == null || Nova.man == null || !Jerrycans.ContainsKey(itemId)) return;
            GasStation station = NearestStation(player.setup.transform.position, EmployeeRange);
            if (station == null) return;
            // On attend la fin de l'ajout dans l'inventaire avant d'y toucher
            Nova.man.StartCoroutine(CheckJerrycan(player, station, itemId, Math.Max(1, number)));
        }

        private IEnumerator CheckJerrycan(Player player, GasStation station, int fullId, int count)
        {
            yield return null;
            if (player?.setup == null || station == null) yield break;

            _links.TryGetValue(station.netId, out GazPump pump);
            int liters = JerrycanLiters * count;
            string reason = null;
            if (pump == null) reason = "Cette pompe est hors service.";
            else if (!pump.Ouverte) reason = $"{pump.Label} est fermée.";
            else if (!IsAllowedAt(player, pump)) reason = $"Seul le personnel de {AllowedBizNames(pump)} peut remplir des jerricans ici.";
            else if (Config.StockActif && pump.Stock < liters) reason = $"Pas assez de carburant dans la citerne de {pump.Label}.";

            if (reason != null)
            {
                int removed = InventoryUtils.RemoveFromInventory(player, fullId, count);
                if (removed > 0) InventoryUtils.AddItem(player, Jerrycans[fullId], removed);
                player.Notify("Station-service", reason, NotificationManager.Type.Error, 6f);
                yield break;
            }

            if (Config.StockActif)
            {
                pump.Stock -= liters;
                _ = pump.Save();
            }
            PendingBill bill = AddPendingBill(player.character.Id, player.character.BizId, pump, liters);
            player.Notify("Station-service", $"Jerrican rempli ({liters} L - {Money(bill.Total)}). À facturer dans « Pleins à facturer ».", NotificationManager.Type.Success, 6f);
        }

        // ==================================================================
        //  Outils
        // ==================================================================

        public double PriceOf(GazPump pump) => pump != null && pump.PrixLitre > 0 ? pump.PrixLitre : Config.PrixLitre;

        public bool IsAllowedBiz(int bizId) => bizId > 0 && Config.BizIdList.Contains(bizId);

        public bool IsAllowedAt(Player player, GazPump pump)
        {
            if (player == null || !player.HasBiz) return false;
            int bizId = player.character.BizId;
            return IsAllowedBiz(bizId) && (pump.BizId == 0 || pump.BizId == bizId);
        }

        private string AllowedBizNames(GazPump pump)
        {
            IEnumerable<int> ids = pump.BizId != 0 ? new[] { pump.BizId } : (IEnumerable<int>)Config.BizIdList;
            List<string> names = ids.Select(BizName).ToList();
            return names.Count > 0 ? string.Join(", ", names) : "l'entreprise autorisée";
        }

        public static string BizName(int bizId)
        {
            Bizs biz = Nova.biz.FetchBiz(bizId);
            return biz != null ? biz.BizName : $"Entreprise #{bizId}";
        }

        private static Player PlayerByCharacter(int characterId)
        {
            return Nova.server.Players.FirstOrDefault(p => p != null && p.character != null && p.character.Id == characterId);
        }

        private static IEnumerable<Player> PlayersNear(Vector3 pos, float range)
        {
            return Nova.server.GetAllInGamePlayers()
                .Where(p => p != null && p.setup != null && Vector3.Distance(p.setup.transform.position, pos) <= range)
                .OrderBy(p => Vector3.Distance(p.setup.transform.position, pos));
        }

        private GasStation NearestStation(Vector3 pos, float range)
        {
            if (_stations.Count == 0 || _stations.Any(s => s == null))
                _stations = UnityEngine.Object.FindObjectsOfType<GasStation>().ToList();
            return _stations
                .Where(s => s != null && Vector3.Distance(s.transform.position, pos) <= range)
                .OrderBy(s => Vector3.Distance(s.transform.position, pos))
                .FirstOrDefault();
        }

        public static string Money(double amount) => amount.ToString("0.00", CultureInfo.InvariantCulture) + " €";

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim().Replace(',', '.').Replace("€", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string OnOff(bool value) => value ? Color("Oui", Colors.Success) : Color("Non", Colors.Error);

        private void AskInput(Player player, string title, string text, string placeholder, Func<string, Task<bool>> onValidate)
        {
            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Input, player, () => AskInput(player, title, text, placeholder, onValidate));
            panel.TextLines.Add(text);
            panel.SetInputPlaceholder(placeholder);
            panel.PreviousButtonWithAction("Valider", async () => await onValidate(panel.inputText?.Trim() ?? ""));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskNumber(Player player, string title, string text, double current, double min, double max, Func<double, Task> onValidate)
        {
            AskInput(player, title, $"{text}\n(entre {min} et {max})", current.ToString(CultureInfo.InvariantCulture), async input =>
            {
                if (!TryParseNumber(input, out double value) || value < min || value > max)
                {
                    player.Notify(Title, "Valeur invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                await onValidate(value);
                player.Notify(Title, "Enregistré.", NotificationManager.Type.Success, 4f);
                return true;
            });
        }

        // ==================================================================
        //  Menu Métier : employés
        // ==================================================================

        public void EmployeeMenu(Player player)
        {
            if (player == null) return;
            if (!_loaded)
            {
                player.Notify(Title, "Le plugin démarre, réessayez dans un instant.", NotificationManager.Type.Warning, 5f);
                return;
            }
            bool admin = player.IsAdmin && player.serviceAdmin;
            if (!player.HasBiz || !IsAllowedBiz(player.character.BizId))
            {
                if (admin)
                {
                    AdminMenu(player);
                    return;
                }
                player.Notify("Station-service", "Votre entreprise n'est pas autorisée à gérer les stations-service.", NotificationManager.Type.Error, 6f);
                return;
            }

            int bizId = player.character.BizId;
            Bizs biz = player.biz;
            int billCount = _bills.TryGetValue(player.character.Id, out List<PendingBill> bills) ? bills.Count : 0;
            List<GazPump> pumps = Pumps.Where(p => p.BizId == 0 || p.BizId == bizId).ToList();

            Panel panel = PanelHelper.Create($"Station-service - {biz?.BizName}", UIPanel.PanelType.Tab, player, () => EmployeeMenu(player));
            panel.AddTabLine($"Pompes ({pumps.Count})", _ => EmployeePumps(player));
            panel.AddTabLine(billCount > 0 ? Color($"Pleins à facturer ({billCount})", Colors.Warning) : "Pleins à facturer (0)", _ => PendingBillsMenu(player));
            panel.AddTabLine("Ventes récentes", async _ => await SalesMenu(player, bizId));
            panel.AddTabLine($"Prix du litre : {Money(Config.PrixLitre)}", _ => { });
            if (admin)
                panel.AddTabLine(Color("Configuration (admin)", Colors.Warning), _ => AdminMenu(player));
            panel.SelectTabButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EmployeePumps(Player player)
        {
            int bizId = player.character.BizId;
            List<GazPump> pumps = Pumps.Where(p => p.BizId == 0 || p.BizId == bizId).OrderBy(p => p.StationName).ThenBy(p => p.PumpName).ToList();

            Panel panel = PanelHelper.Create("Station-service - Pompes", UIPanel.PanelType.TabPrice, player, () => EmployeePumps(player));
            if (pumps.Count == 0)
                panel.AddTabLine("Aucune pompe attribuée", _ => { });
            foreach (GazPump pump in pumps)
            {
                string state = pump.Ouverte ? Color("Ouverte", Colors.Success) : Color("Fermée", Colors.Error);
                string stock = Config.StockActif ? $" - {pump.Stock}/{Config.CapaciteCiterne} L" : "";
                panel.AddTabLine($"{pump.Label}{stock}", $"{state} {Money(PriceOf(pump))}", IconUtils.Others.None.Id, _ => EmployeePump(player, pump));
            }
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EmployeePump(Player player, GazPump pump)
        {
            bool isBoss = player.biz != null && player.biz.OwnerId == player.character.Id;

            Panel panel = PanelHelper.Create($"Station-service - {pump.Label}", UIPanel.PanelType.Tab, player, () => EmployeePump(player, pump));
            panel.AddTabLine(pump.Ouverte ? Color("Fermer la pompe", Colors.Error) : Color("Ouvrir la pompe", Colors.Success), async ui =>
            {
                pump.Ouverte = !pump.Ouverte;
                await pump.Save();
                player.Notify("Station-service", $"{pump.Label} {(pump.Ouverte ? "ouverte" : "fermée")}.", NotificationManager.Type.Success, 4f);
                panel.Refresh();
            });
            panel.AddTabLine($"Prix : {Money(PriceOf(pump))} / L", _ => { });
            if (Config.StockActif)
            {
                panel.AddTabLine($"Citerne : {pump.Stock} / {Config.CapaciteCiterne} L", _ => { });
                panel.AddTabLine(isBoss ? "Commander du carburant" : Color("Commander du carburant (patron)", Colors.Grey), _ =>
                {
                    if (!isBoss)
                    {
                        player.Notify("Station-service", "Seul le patron peut commander du carburant.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    OrderFuel(player, pump);
                });
            }
            panel.AddTabLine($"Vendu : {pump.LitresVendus} L - {Money(pump.ChiffreAffaires)}", _ => { });
            panel.AddTabLine("Mettre le GPS sur la pompe", _ =>
            {
                player.setup.TargetSetGPSTarget(pump.Position);
                player.Notify("Station-service", "GPS défini.", NotificationManager.Type.Info, 4f);
            });
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void OrderFuel(Player player, GazPump pump)
        {
            int free = Math.Max(0, Config.CapaciteCiterne - pump.Stock);
            if (free <= 0)
            {
                player.Notify("Station-service", "La citerne est déjà pleine.", NotificationManager.Type.Warning, 5f);
                return;
            }
            AskInput(player, "Commander du carburant",
                $"Citerne : {pump.Stock} / {Config.CapaciteCiterne} L\nPrix d'achat : {Money(Config.PrixAchatLitre)} / L (payé par l'entreprise)\nCombien de litres ?",
                $"1 - {free}", async input =>
                {
                    Bizs biz = player.biz;
                    if (!int.TryParse(input, out int liters) || liters <= 0 || liters > free)
                    {
                        player.Notify("Station-service", $"Quantité invalide (1 - {free}).", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    double cost = Math.Round(liters * Config.PrixAchatLitre, 2);
                    if (biz == null || biz.Bank < cost)
                    {
                        player.Notify("Station-service", $"L'entreprise n'a pas assez d'argent ({Money(cost)}).", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    biz.AddBankMoney(-cost, $"Achat carburant {liters} L ({pump.Label})");
                    pump.Stock += liters;
                    await pump.Save();
                    player.Notify("Station-service", $"{liters} L livrés dans {pump.Label} pour {Money(cost)}.", NotificationManager.Type.Success, 5f);
                    return true;
                });
        }

        // ------------------------------------------------------------------
        //  Facturation
        // ------------------------------------------------------------------

        private void PendingBillsMenu(Player player)
        {
            _bills.TryGetValue(player.character.Id, out List<PendingBill> bills);
            bills ??= new List<PendingBill>();

            Panel panel = PanelHelper.Create("Pleins à facturer", UIPanel.PanelType.TabPrice, player, () => PendingBillsMenu(player));
            if (bills.Count == 0)
                panel.AddTabLine("Aucun plein en attente", _ => { });
            foreach (PendingBill bill in bills.ToList())
                panel.AddTabLine($"{bill.Pump.Label} - {bill.Liters} L", Money(bill.Total), IconUtils.Others.None.Id, _ => BillMenu(player, bill));
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void BillMenu(Player employee, PendingBill bill)
        {
            List<Player> clients = PlayersNear(employee.setup.transform.position, ClientRange).Where(p => p != employee).ToList();

            Panel panel = PanelHelper.Create($"Facturer {bill.Liters} L - {Money(bill.Total)}", UIPanel.PanelType.Tab, employee, () => BillMenu(employee, bill));
            if (clients.Count == 0)
                panel.AddTabLine("Aucun client à proximité", _ => { });
            foreach (Player client in clients)
                panel.AddTabLine(client.FullName, _ => SendBill(employee, client, bill));
            if (clients.Count > 0)
                panel.AddButton("Facturer", _ => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.AddButton(Color("Offrir", Colors.Warning), _ =>
            {
                RemoveBill(employee.character.Id, bill);
                employee.Notify("Station-service", "Plein offert, aucune facture envoyée.", NotificationManager.Type.Info, 5f);
                panel.Close();
            });
            panel.CloseButton("Plus tard");
            panel.Display();
        }

        private void SendBill(Player employee, Player client, PendingBill bill)
        {
            bill.ClientCharacterId = client.character.Id;
            employee.Notify("Station-service", $"Facture de {Money(bill.Total)} envoyée à {client.FullName}.", NotificationManager.Type.Info, 5f);
            ClientBillPanel(client, employee.character.Id, employee.FullName, bill);
        }

        private void ClientBillPanel(Player client, int employeeId, string employeeName, PendingBill bill)
        {
            Panel panel = PanelHelper.Create("Station-service - Facture", UIPanel.PanelType.Text, client, () => ClientBillPanel(client, employeeId, employeeName, bill));
            panel.TextLines.Add($"{Color(BizName(bill.BizId), Colors.Info)} - {bill.Pump.Label}");
            panel.TextLines.Add($"Carburant : {bill.Liters} L x {Money(bill.PricePerLiter)}");
            panel.TextLines.Add($"Total : {Color(Money(bill.Total), Colors.Warning)}");
            panel.TextLines.Add($"Servi par : {employeeName}");
            panel.CloseButtonWithAction("Payer en espèces", () => Task.FromResult(Pay(client, employeeId, bill, true)));
            panel.CloseButtonWithAction("Payer par carte", () => Task.FromResult(Pay(client, employeeId, bill, false)));
            panel.CloseButtonWithAction(Color("Refuser", Colors.Error), () =>
            {
                if (bill.ClientCharacterId == client.character.Id)
                {
                    bill.ClientCharacterId = 0;
                    PlayerByCharacter(employeeId)?.Notify("Station-service", $"{client.FullName} a refusé de payer {Money(bill.Total)}.", NotificationManager.Type.Warning, 6f);
                }
                return Task.FromResult(true);
            });
            panel.Display();
        }

        private bool Pay(Player client, int employeeId, PendingBill bill, bool cash)
        {
            if (bill.ClientCharacterId != client.character.Id || !_bills.TryGetValue(employeeId, out List<PendingBill> list) || !list.Contains(bill))
            {
                client.Notify("Station-service", "Cette facture n'est plus valable.", NotificationManager.Type.Error, 5f);
                return true;
            }

            double total = bill.Total;
            if ((cash ? client.Money : client.Bank) < total)
            {
                client.Notify("Station-service", cash ? "Vous n'avez pas assez d'espèces." : "Vous n'avez pas assez d'argent en banque.", NotificationManager.Type.Error, 5f);
                return false;
            }

            if (cash) client.AddMoney(-total, "Station-service");
            else client.AddBankMoney(-total, "Station-service");
            list.Remove(bill);

            Player employee = PlayerByCharacter(employeeId);
            double commission = employee != null ? Math.Round(total * Config.CommissionPourcent / 100.0, 2) : 0;
            Nova.biz.FetchBiz(bill.BizId)?.AddBankMoney(total - commission, $"Vente carburant {bill.Liters} L ({bill.Pump.Label})");
            if (commission > 0)
                employee.AddBankMoney(commission, "Prime station-service");

            bill.Pump.LitresVendus += bill.Liters;
            bill.Pump.ChiffreAffaires += total;
            _ = bill.Pump.Save();
            _ = new GazSale
            {
                PumpId = bill.Pump.Id,
                PumpLabel = bill.Pump.Label,
                BizId = bill.BizId,
                EmployeeName = employee?.FullName ?? "?",
                ClientName = client.FullName,
                Litres = bill.Liters,
                Montant = total,
                Date = Nova.UnixTimeNow(),
            }.Save();

            client.Notify("Station-service", $"Vous avez payé {Money(total)}. Bonne route !", NotificationManager.Type.Success, 5f);
            employee?.Notify("Station-service", $"{client.FullName} a payé {Money(total)}" + (commission > 0 ? $" (prime : {Money(commission)})" : "") + ".", NotificationManager.Type.Success, 6f);
            return true;
        }

        private void RemoveBill(int employeeId, PendingBill bill)
        {
            if (_bills.TryGetValue(employeeId, out List<PendingBill> list))
                list.Remove(bill);
        }

        private async Task SalesMenu(Player player, int bizId)
        {
            List<GazSale> sales = bizId > 0 ? await GazSale.Query(s => s.BizId == bizId) : await GazSale.QueryAll();
            sales = (sales ?? new List<GazSale>()).OrderByDescending(s => s.Date).Take(30).ToList();

            Panel panel = PanelHelper.Create("Ventes récentes", UIPanel.PanelType.TabPrice, player, async () => await SalesMenu(player, bizId));
            if (sales.Count == 0)
                panel.AddTabLine("Aucune vente", _ => { });
            foreach (GazSale sale in sales)
            {
                string date = Nova.UnixTimeToDateTime(sale.Date).ToString("dd/MM HH:mm");
                panel.AddTabLine($"{date} - {sale.ClientName} - {sale.Litres} L ({sale.EmployeeName})", Money(sale.Montant), IconUtils.Others.None.Id, _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Configuration (admin)
        // ==================================================================

        private bool CheckAdmin(Player player)
        {
            if (player != null && player.IsAdmin && player.serviceAdmin) return true;
            player?.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
            return false;
        }

        public void AdminMenu(Player player)
        {
            if (!CheckAdmin(player)) return;
            if (!_loaded)
            {
                player.Notify(Title, "Le plugin démarre, réessayez dans un instant.", NotificationManager.Type.Warning, 5f);
                return;
            }

            Panel panel = PanelHelper.Create(Title, UIPanel.PanelType.Tab, player, () => AdminMenu(player));
            panel.AddTabLine($"Entreprises autorisées ({Config.BizIdList.Count})", _ => AdminBizs(player));
            panel.AddTabLine($"Pompes ({Pumps.Count})", _ => AdminPumps(player));
            panel.AddTabLine("Ajouter la pompe la plus proche", _ => AddNearestPump(player));
            panel.AddTabLine("Ajouter toutes les pompes de la carte", async _ => await AddAllPumps(player));
            panel.AddTabLine($"Prix du litre : {Money(Config.PrixLitre)}", _ =>
                AskNumber(player, "Prix du litre", "Prix de vente du litre d'essence (€)", Config.PrixLitre, 0, 1000, async v =>
                {
                    Config.PrixLitre = Math.Round(v, 2);
                    await Config.Save();
                    _nextScan = 0f;
                }));
            panel.AddTabLine($"Prime employé : {Config.CommissionPourcent} %", _ =>
                AskNumber(player, "Prime employé", "Pourcentage de chaque vente reversé à l'employé qui a servi", Config.CommissionPourcent, 0, 100, async v =>
                {
                    Config.CommissionPourcent = (int)v;
                    await Config.Save();
                }));
            panel.AddTabLine($"Gestion du stock : {OnOff(Config.StockActif)}", async _ =>
            {
                Config.StockActif = !Config.StockActif;
                await Config.Save();
                panel.Refresh();
            });
            if (Config.StockActif)
            {
                panel.AddTabLine($"Prix d'achat du litre (stock) : {Money(Config.PrixAchatLitre)}", _ =>
                    AskNumber(player, "Prix d'achat", "Prix payé par l'entreprise pour remplir ses citernes (€ / L)", Config.PrixAchatLitre, 0, 1000, async v =>
                    {
                        Config.PrixAchatLitre = Math.Round(v, 2);
                        await Config.Save();
                    }));
                panel.AddTabLine($"Capacité des citernes : {Config.CapaciteCiterne} L", _ =>
                    AskNumber(player, "Capacité des citernes", "Litres maximum par pompe", Config.CapaciteCiterne, 1, 1000000, async v =>
                    {
                        Config.CapaciteCiterne = (int)v;
                        await Config.Save();
                    }));
            }
            panel.AddTabLine("Toutes les ventes", async _ => await SalesMenu(player, 0));
            panel.SelectTabButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminBizs(Player player)
        {
            List<Bizs> bizs = Nova.biz.bizs.OrderBy(b => b.BizName).ToList();

            Panel panel = PanelHelper.Create("Entreprises autorisées", UIPanel.PanelType.Tab, player, () => AdminBizs(player));
            if (bizs.Count == 0)
                panel.AddTabLine("Aucune entreprise sur le serveur", _ => { });
            foreach (Bizs biz in bizs)
            {
                bool allowed = Config.BizIdList.Contains(biz.Id);
                panel.AddTabLine($"{(allowed ? Color("[Autorisée]", Colors.Success) : Color("[Non]", Colors.Grey))} [{biz.Id}] {biz.BizName}", async _ =>
                {
                    List<int> ids = Config.BizIdList;
                    if (allowed) ids.Remove(biz.Id);
                    else ids.Add(biz.Id);
                    Config.BizIdList = ids;
                    await Config.Save();
                    panel.Refresh();
                });
            }
            panel.AddButton("Autoriser / Retirer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminPumps(Player player)
        {
            List<GazPump> pumps = Pumps.OrderBy(p => p.StationName).ThenBy(p => p.PumpName).ToList();

            Panel panel = PanelHelper.Create("Pompes configurées", UIPanel.PanelType.TabPrice, player, () => AdminPumps(player));
            if (pumps.Count == 0)
                panel.AddTabLine("Aucune pompe : utilisez « Ajouter »", _ => { });
            foreach (GazPump pump in pumps)
            {
                string owner = pump.BizId == 0 ? "Toutes" : BizName(pump.BizId);
                panel.AddTabLine($"{pump.Label} ({owner})", Money(PriceOf(pump)), IconUtils.Others.None.Id, _ => AdminPump(player, pump));
            }
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminPump(Player player, GazPump pump)
        {
            Panel panel = PanelHelper.Create($"Pompe - {pump.Label}", UIPanel.PanelType.Tab, player, () => AdminPump(player, pump));
            panel.AddTabLine($"Nom : {pump.PumpName}", _ =>
                AskInput(player, "Nom de la pompe", "Nouveau nom (ex. Pompe 1)", pump.PumpName, async input =>
                {
                    if (input.Length == 0) return false;
                    pump.PumpName = input;
                    return await pump.Save();
                }));
            panel.AddTabLine($"Station : {pump.StationName}", _ =>
                AskInput(player, "Nom de la station", "Nom de la station (ex. Station du port)", pump.StationName, async input =>
                {
                    if (input.Length == 0) return false;
                    pump.StationName = input;
                    return await pump.Save();
                }));
            panel.AddTabLine($"Entreprise : {(pump.BizId == 0 ? "toutes les entreprises autorisées" : BizName(pump.BizId))}", _ => AdminPumpBiz(player, pump));
            panel.AddTabLine($"Prix : {(pump.PrixLitre > 0 ? Money(pump.PrixLitre) : $"prix global ({Money(Config.PrixLitre)})")}", _ =>
                AskNumber(player, "Prix de la pompe", "Prix du litre pour cette pompe (0 = prix global)", pump.PrixLitre, 0, 1000, async v =>
                {
                    pump.PrixLitre = Math.Round(v, 2);
                    await pump.Save();
                }));
            if (Config.StockActif)
                panel.AddTabLine($"Citerne : {pump.Stock} / {Config.CapaciteCiterne} L", _ =>
                    AskNumber(player, "Citerne", "Litres dans la citerne", pump.Stock, 0, Config.CapaciteCiterne, async v =>
                    {
                        pump.Stock = (int)v;
                        await pump.Save();
                    }));
            panel.AddTabLine($"Ouverte : {OnOff(pump.Ouverte)}", async _ =>
            {
                pump.Ouverte = !pump.Ouverte;
                await pump.Save();
                panel.Refresh();
            });
            panel.AddTabLine($"Vendu : {pump.LitresVendus} L - {Money(pump.ChiffreAffaires)}", _ => { });
            panel.AddTabLine("Se téléporter", _ => player.setup.TargetSetPosition(pump.Position + Vector3.up));
            panel.AddTabLine(Color("Supprimer la pompe", Colors.Error), async _ =>
            {
                if (await pump.Delete())
                {
                    Pumps.Remove(pump);
                    RebuildLinks();
                    player.Notify(Title, $"{pump.Label} supprimée : elle est de nouveau bloquée.", NotificationManager.Type.Success, 5f);
                    panel.Previous();
                }
            });
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminPumpBiz(Player player, GazPump pump)
        {
            Panel panel = PanelHelper.Create("Entreprise de la pompe", UIPanel.PanelType.Tab, player, () => AdminPumpBiz(player, pump));
            panel.AddTabLine("Toutes les entreprises autorisées", async _ => await SetPumpBiz(player, pump, 0, panel));
            foreach (int bizId in Config.BizIdList)
                panel.AddTabLine($"[{bizId}] {BizName(bizId)}", async _ => await SetPumpBiz(player, pump, bizId, panel));
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task SetPumpBiz(Player player, GazPump pump, int bizId, Panel panel)
        {
            pump.BizId = bizId;
            await pump.Save();
            player.Notify(Title, "Entreprise de la pompe modifiée.", NotificationManager.Type.Success, 4f);
            panel.Previous();
        }

        private void AddNearestPump(Player player)
        {
            GasStation station = NearestStation(player.setup.transform.position, 6f);
            if (station == null)
            {
                player.Notify(Title, "Aucune pompe du jeu à moins de 6 m. Approchez-vous d'une pompe.", NotificationManager.Type.Error, 5f);
                return;
            }
            Vector3 pos = station.transform.position;
            GazPump existing = Pumps.FirstOrDefault(p => Vector3.Distance(p.Position, pos) <= LinkRange);
            if (existing != null)
            {
                player.Notify(Title, $"Cette pompe est déjà configurée : {existing.Label}.", NotificationManager.Type.Warning, 5f);
                AdminPump(player, existing);
                return;
            }

            // Reprend le nom de la station la plus proche s'il y en a une
            GazPump neighbour = Pumps.Where(p => Vector3.Distance(p.Position, pos) <= StationClusterRange)
                .OrderBy(p => Vector3.Distance(p.Position, pos)).FirstOrDefault();
            string stationName = neighbour?.StationName ?? $"Station {Pumps.Select(p => p.StationName).Distinct().Count() + 1}";
            int number = Pumps.Count(p => p.StationName == stationName) + 1;

            AskInput(player, "Nouvelle pompe", $"Station : {stationName}\nNom de la pompe :", $"Pompe {number}", async input =>
            {
                GazPump pump = new GazPump
                {
                    StationName = stationName,
                    PumpName = input.Length > 0 ? input : $"Pompe {number}",
                    Position = pos,
                    Stock = Config.StockActif ? 0 : Config.CapaciteCiterne,
                };
                if (!await pump.Save()) return false;
                Pumps.Add(pump);
                RebuildLinks();
                player.Notify(Title, $"{pump.Label} ajoutée. Pensez à choisir l'entreprise et à remplir la citerne.", NotificationManager.Type.Success, 6f);
                return true;
            });
        }

        private async Task AddAllPumps(Player player)
        {
            _stations = UnityEngine.Object.FindObjectsOfType<GasStation>().ToList();
            List<GasStation> fresh = _stations
                .Where(s => s != null && !Pumps.Any(p => Vector3.Distance(p.Position, s.transform.position) <= LinkRange))
                .ToList();

            int added = 0;
            foreach (GasStation station in fresh)
            {
                Vector3 pos = station.transform.position;
                // Même station que la pompe déjà configurée la plus proche (< 30 m), sinon nouvelle station
                GazPump neighbour = Pumps.Where(p => Vector3.Distance(p.Position, pos) <= StationClusterRange)
                    .OrderBy(p => Vector3.Distance(p.Position, pos)).FirstOrDefault();
                string stationName = neighbour?.StationName ?? $"Station {Pumps.Select(p => p.StationName).Distinct().Count() + 1}";

                GazPump pump = new GazPump
                {
                    StationName = stationName,
                    PumpName = $"Pompe {Pumps.Count(p => p.StationName == stationName) + 1}",
                    Position = pos,
                    Stock = Config.StockActif ? 0 : Config.CapaciteCiterne,
                };
                if (await pump.Save())
                {
                    Pumps.Add(pump);
                    added++;
                }
            }
            RebuildLinks();
            player.Notify(Title, $"{added} pompe(s) ajoutée(s) sur {_stations.Count} trouvée(s) sur la carte.", NotificationManager.Type.Success, 6f);
            AdminPumps(player);
        }
    }

    // ======================================================================
    //  Données en mémoire
    // ======================================================================

    /// <summary>Un pistolet branché sur un véhicule par un employé autorisé.</summary>
    internal class FillSession
    {
        public GazPump Pump;
        public int EmployeeCharacterId;
        public int BizId;
        public uint VehicleNetId;
        public bool IsElectric;
        public int Liters;
    }

    /// <summary>Plein terminé, en attente de paiement par un client.</summary>
    internal class PendingBill
    {
        public GazPump Pump;
        public int BizId;
        public int Liters;
        public double PricePerLiter;
        public int ClientCharacterId;
        public double Total => Math.Round(Liters * PricePerLiter, 2);
    }

    // ======================================================================
    //  Données sauvegardées (base ModKit)
    // ======================================================================

    public class GazConfig : ModEntity<GazConfig>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public double PrixLitre { get; set; } = 2.0;
        /// <summary>Ids des entreprises autorisées, séparés par des virgules.</summary>
        public string BizIds { get; set; } = "";
        public bool StockActif { get; set; } = true;
        public double PrixAchatLitre { get; set; } = 1.0;
        public int CapaciteCiterne { get; set; } = 1000;
        public int CommissionPourcent { get; set; } = 10;

        [Ignore]
        public List<int> BizIdList
        {
            get => (BizIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out int id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            set => BizIds = string.Join(",", value.Distinct());
        }
    }

    public class GazPump : ModEntity<GazPump>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string StationName { get; set; }
        public string PumpName { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        /// <summary>0 = toutes les entreprises autorisées.</summary>
        public int BizId { get; set; }
        /// <summary>0 = prix global.</summary>
        public double PrixLitre { get; set; }
        public bool Ouverte { get; set; } = true;
        public int Stock { get; set; }
        public int LitresVendus { get; set; }
        public double ChiffreAffaires { get; set; }

        [Ignore]
        public Vector3 Position
        {
            get => new Vector3(X, Y, Z);
            set { X = value.x; Y = value.y; Z = value.z; }
        }

        [Ignore]
        public string Label => $"{StationName} - {PumpName}";
    }

    public class GazSale : ModEntity<GazSale>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int PumpId { get; set; }
        public string PumpLabel { get; set; }
        public int BizId { get; set; }
        public string EmployeeName { get; set; }
        public string ClientName { get; set; }
        public int Litres { get; set; }
        public double Montant { get; set; }
        public long Date { get; set; }
    }
}
