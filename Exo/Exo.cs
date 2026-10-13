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
using AAMenu.Classes;
using Mirror;
using ModKit.Helper.JobHelper;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using Logger = ModKit.Internal.Logger;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace Exo
{
    /// <summary>
    /// EXO By Loris Strange : les pompes à essence du jeu sont bloquées, seules les
    /// entreprises autorisées peuvent servir de l'essence et facturer les clients.
    /// </summary>
    public class ExoPlugin : ModKit.ModKit
    {
        public const string Title = "EXO By Loris Strange";

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

        public ExoConfig Config { get; private set; }
        public List<ExoPump> Pumps { get; } = new List<ExoPump>();

        private bool _loaded;
        private bool _menuRegistered;
        private bool _refreshingLines;
        private readonly List<ModTabLines.ModTabLine> _bizLines = new List<ModTabLines.ModTabLine>();
        private Coroutine _loop;

        private List<GasStation> _stations = new List<GasStation>();
        private readonly Dictionary<uint, ExoPump> _links = new Dictionary<uint, ExoPump>();
        private float _nextScan;

        private readonly Dictionary<(uint station, int slot), FillSession> _sessions = new Dictionary<(uint, int), FillSession>();
        private readonly Dictionary<int, List<PendingBill>> _bills = new Dictionary<int, List<PendingBill>>();
        private readonly Dictionary<uint, float> _lastBlockNotify = new Dictionary<uint, float>();

        /// <summary>Personnages en service (prise de service dans le menu Métier).</summary>
        private readonly HashSet<int> _onDuty = new HashSet<int>();
        /// <summary>Paiements d'avance à l'automate, par pompe.</summary>
        private readonly Dictionary<int, Prepay> _prepays = new Dictionary<int, Prepay>();
        private float _nextPrepayCheck;
        private readonly ExoAutomatePattern _automatePattern = new ExoAutomatePattern();

        /// <summary>Durée pendant laquelle une pompe payée à l'automate reste débloquée.</summary>
        private const float PrepayDuration = 300f;

        public static ExoPlugin Instance { get; private set; }

        public ExoPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.1.0", "Loris Strange");
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            PointHelper.AddPattern(nameof(ExoAutomatePattern), _automatePattern);
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
            // Une reconnexion termine le service
            if (character != null) _onDuty.Remove(character.Id);
            PointHelper.InitAllNPoint(player);
            TryRegisterMenu();
            EnsureLoop();
        }

        private void OnMinutePassed()
        {
            TryRegisterMenu();
            EnsureLoop();
            // Suit les changements d'activité des entreprises
            _ = RefreshBizLines();
        }

        private async Task LoadAsync()
        {
            try
            {
                await Orm.SqliteConnection.CreateTableAsync<ExoConfig>();
                await Orm.SqliteConnection.CreateTableAsync<ExoPump>();
                await Orm.SqliteConnection.CreateTableAsync<ExoSale>();
                await Orm.SqliteConnection.CreateTableAsync<ExoAutomatePattern>();

                Config = (await ExoConfig.QueryAll())?.FirstOrDefault();
                if (Config == null)
                {
                    Config = new ExoConfig();
                    await Config.Save();
                }

                Pumps.Clear();
                Pumps.AddRange(await ExoPump.QueryAll() ?? new List<ExoPump>());
                _loaded = true;
                _ = RefreshBizLines();
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

            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, Title,
                ui => AdminMenu(PanelHelper.ReturnPlayerFromPanel(ui)));
            // Administration > Points bleus > Automate essence
            AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Automate essence", _automatePattern, this);
            _ = RefreshBizLines();
        }

        /// <summary>
        /// AAMenu ne filtre les lignes du menu Métier que par activité (ou activité personnalisée ModKit),
        /// pas par entreprise. On crée donc la ligne « Station-service » uniquement pour les activités
        /// des entreprises autorisées, et on la recrée quand la liste change.
        /// </summary>
        private async Task RefreshBizLines()
        {
            if (!_menuRegistered || AAMenu.AAMenu.menu == null || Config == null || _refreshingLines) return;
            _refreshingLines = true;
            try
            {
                HashSet<Activity.Type> types = new HashSet<Activity.Type>();
                Dictionary<string, CustomActivity> customs = new Dictionary<string, CustomActivity>();
                foreach (int bizId in Config.BizIdList)
                {
                    if (Nova.biz.FetchBiz(bizId) == null) continue;
                    CustomBiz customBiz = (await CustomBiz.Query(c => c.BizId == bizId))?.FirstOrDefault();
                    CustomActivity custom = customBiz != null ? await CustomActivity.Query(customBiz.CustomActivityId) : null;
                    if (custom != null)
                    {
                        customs[custom.Name] = custom;
                        continue;
                    }
                    Activity.Type type = Nova.biz.GetBizActivities(bizId).FirstOrDefault();
                    // None rendrait la ligne visible pour tout le monde : ces entreprises passent par /exo
                    if (type != Activity.Type.None) types.Add(type);
                }

                List<ModTabLines.ModTabLine> lines = new List<ModTabLines.ModTabLine>();
                if (types.Count > 0)
                    lines.Add(NewBizLine(types.ToList(), null));
                foreach (CustomActivity custom in customs.Values)
                    lines.Add(NewBizLine(null, custom));

                // BizPanel remplace la liste à chaque ouverture : toujours relire la propriété
                AAMenu.AAMenu.menu.BizTabLines.RemoveAll(l => _bizLines.Contains(l));
                AAMenu.AAMenu.menu.BizTabLines.AddRange(lines);
                _bizLines.Clear();
                _bizLines.AddRange(lines);
            }
            catch (Exception ex)
            {
                Logger.LogError(Title, "Menu Métier : " + ex);
            }
            finally
            {
                _refreshingLines = false;
            }
        }

        private ModTabLines.ModTabLine NewBizLine(List<Activity.Type> types, CustomActivity custom)
        {
            return new ModTabLines.ModTabLine(PluginInformations, "Station-service", ui => EmployeeMenu(PanelHelper.ReturnPlayerFromPanel(ui)))
            {
                ActivitiesAllowed = types,
                CustomActivity = custom,
                Index = 100,
            };
        }

        private void RegisterCommands()
        {
            // Secours si AAMenu n'est pas installé
            new SChatCommand("/exo", "Station-service (" + Title + ")", "/exo",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (player.IsAdmin && player.serviceAdmin) AdminMenu(player);
                    else EmployeeMenu(player);
                })).Register();

            new SChatCommand("/exowebhook", "Définir le webhook Discord de " + Title, "/exowebhook <url | 0>",
                (Action<Player, string[]>)(async (player, args) =>
                {
                    if (!CheckAdmin(player) || Config == null) return;
                    if (args.Length == 0)
                    {
                        player.Notify(Title, "Utilisation : /exowebhook <url> (ou 0 pour retirer)", NotificationManager.Type.Info, 6f);
                        return;
                    }
                    Config.WebhookUrl = args[0] == "0" ? "" : args[0].Trim();
                    await Config.Save();
                    player.Notify(Title, HasWebhook ? "Webhook enregistré." : "Webhook retiré.", NotificationManager.Type.Success, 5f);
                    if (HasWebhook) Log("✅ Webhook configuré", "#3fd2ff", $"Configuré par {player.FullName}.", ("Pompes", Pumps.Count.ToString()));
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

            if (Time.realtimeSinceStartup >= _nextPrepayCheck)
            {
                _nextPrepayCheck = Time.realtimeSinceStartup + 1f;
                ExpirePrepays();
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
                ExoPump pump = Pumps
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
                _links.TryGetValue(station.netId, out ExoPump pump);
                Player employee = FindEmployee(station, pump, out string reason);
                Prepay prepay = employee == null ? FindPrepay(station, pump) : null;
                if (employee == null && prepay == null)
                {
                    Block(station, slot, vehicle, delivered, reason);
                    return;
                }

                if (employee != null)
                {
                    session = new FillSession
                    {
                        Pump = pump,
                        EmployeeCharacterId = employee.character.Id,
                        BizId = employee.character.BizId,
                        VehicleNetId = attached,
                        IsElectric = station.isElectric,
                    };
                    employee.Notify("Station-service", $"Remplissage démarré ({pump.Label}) - {Money(PriceOf(pump))}/L", NotificationManager.Type.Info, 5f);
                }
                else
                {
                    // Pompe payée d'avance à l'automate
                    prepay.InUse = true;
                    session = new FillSession
                    {
                        Pump = pump,
                        EmployeeCharacterId = prepay.CharacterId,
                        BizId = prepay.BizId,
                        VehicleNetId = attached,
                        IsElectric = station.isElectric,
                        Prepay = prepay,
                        MaxLiters = prepay.MaxLiters,
                    };
                    PlayerByCharacter(prepay.CharacterId)?.Notify("Automate",
                        $"Remplissage en cours : {prepay.MaxLiters} L maximum ({Money(prepay.Amount)}).", NotificationManager.Type.Info, 5f);
                }
                _sessions[key] = session;
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

            if (session.MaxLiters > 0 && session.Liters >= session.MaxLiters)
            {
                // Montant payé à l'automate atteint : on raccroche
                session.Liters = session.MaxLiters;
                Detach(station, slot, vehicle);
                _sessions.Remove(key);
                PlayerByCharacter(session.EmployeeCharacterId)?.Notify("Automate",
                    "Montant atteint : le pistolet a été raccroché.", NotificationManager.Type.Success, 6f);
                EndSession(session);
                return;
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
        private Player FindEmployee(GasStation station, ExoPump pump, out string reason)
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
            List<Player> near = PlayersNear(pos, EmployeeRange).ToList();
            Player employee = near.FirstOrDefault(p => IsAllowedAt(p, pump) && IsOnDuty(p));
            if (employee == null)
            {
                if (near.Any(p => IsAllowedAt(p, pump)))
                    reason = "Prenez votre service (Métier → Station-service) pour servir de l'essence.";
                else if (!Config.AutomateDesactive && !StationStaffed(pump.StationName))
                    reason = "Aucun employé en service : payez votre essence à l'automate (point bleu) de la station.";
                else
                    reason = $"Seul le personnel de {AllowedBizNames(pump)} peut servir de l'essence ici.";
            }
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
            if (session.Prepay != null)
            {
                // Pistolet raccroché sans essence : la pompe reste payée
                if (session.Liters <= 0) session.Prepay.InUse = false;
                else EndSelfService(session);
                return;
            }
            if (session.Liters <= 0) return;

            ExoPump pump = session.Pump;
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

        private PendingBill AddPendingBill(int employeeId, int bizId, ExoPump pump, int liters)
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

            _links.TryGetValue(station.netId, out ExoPump pump);
            int liters = JerrycanLiters * count;
            string reason = null;
            if (pump == null) reason = "Cette pompe est hors service.";
            else if (!pump.Ouverte) reason = $"{pump.Label} est fermée.";
            else if (!IsAllowedAt(player, pump)) reason = $"Seul le personnel de {AllowedBizNames(pump)} peut remplir des jerricans ici.";
            else if (!IsOnDuty(player)) reason = "Prenez votre service pour remplir des jerricans.";
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
        //  Prise de service
        // ==================================================================

        public bool IsOnDuty(Player player) => player?.character != null && _onDuty.Contains(player.character.Id);

        private void SetDuty(Player player, bool onDuty)
        {
            if (onDuty) _onDuty.Add(player.character.Id);
            else _onDuty.Remove(player.character.Id);
            player.Notify("Station-service", onDuty ? "Vous avez pris votre service." : "Vous avez terminé votre service.",
                onDuty ? NotificationManager.Type.Success : NotificationManager.Type.Info, 5f);
            Log(onDuty ? "🟢 Prise de service" : "🔴 Fin de service", onDuty ? "#3ddc84" : "#ff4d5e",
                $"{player.FullName} {(onDuty ? "a pris" : "a terminé")} son service.", ("Entreprise", BizName(player.character.BizId)));
        }

        /// <summary>Un employé autorisé est-il en service pour au moins une pompe de cette station ?</summary>
        public bool StationStaffed(string stationName)
        {
            List<ExoPump> pumps = Pumps.Where(p => p.StationName == stationName).ToList();
            return Nova.server.GetAllInGamePlayers().Any(pl => pl?.character != null && IsOnDuty(pl) && pumps.Any(pp => IsAllowedAt(pl, pp)));
        }

        // ==================================================================
        //  Automate (point bleu) : paiement d'avance quand personne n'est en service
        // ==================================================================

        private Prepay FindPrepay(GasStation station, ExoPump pump)
        {
            if (pump == null || !pump.Ouverte || (Config.StockActif && pump.Stock <= 0)) return null;
            if (!_prepays.TryGetValue(pump.Id, out Prepay prepay)) return null;
            Player buyer = PlayerByCharacter(prepay.CharacterId);
            if (buyer?.setup == null || Vector3.Distance(buyer.setup.transform.position, station.transform.position) > EmployeeRange)
                return null;
            return prepay;
        }

        private void ExpirePrepays()
        {
            float now = Time.realtimeSinceStartup;
            foreach (Prepay prepay in _prepays.Values.Where(p => !p.InUse && now > p.Expires).ToList())
            {
                _prepays.Remove(prepay.PumpId);
                Player buyer = PlayerByCharacter(prepay.CharacterId);
                RefundPrepay(prepay, buyer, prepay.Amount);
                buyer?.Notify("Automate", $"Temps écoulé : {prepay.PumpLabel} est de nouveau bloquée. Vous avez été remboursé ({Money(prepay.Amount)}).", NotificationManager.Type.Warning, 7f);
                Log("⌛ Automate : temps écoulé", "#ffc93c", $"{prepay.ClientName} n'a pas utilisé la pompe payée.",
                    ("Pompe", prepay.PumpLabel), ("Remboursé", Money(prepay.Amount)));
            }
        }

        private static void RefundPrepay(Prepay prepay, Player buyer, double amount)
        {
            if (buyer == null || amount < 0.01) return;
            if (prepay.Cash) buyer.AddMoney(amount, "Remboursement automate essence");
            else buyer.AddBankMoney(amount, "Remboursement automate essence");
        }

        private void EndSelfService(FillSession session)
        {
            Prepay prepay = session.Prepay;
            _prepays.Remove(prepay.PumpId);
            ExoPump pump = session.Pump;

            int liters = Math.Min(session.Liters, prepay.MaxLiters);
            double cost = Math.Min(prepay.Amount, Math.Round(liters * prepay.PricePerLiter, 2));
            double refund = Math.Round(prepay.Amount - cost, 2);
            Player buyer = PlayerByCharacter(prepay.CharacterId);
            RefundPrepay(prepay, buyer, refund);

            if (Config.StockActif) pump.Stock = Math.Max(0, pump.Stock - liters);
            pump.LitresVendus += liters;
            pump.ChiffreAffaires += cost;
            _ = pump.Save();
            Nova.biz.FetchBiz(prepay.BizId)?.AddBankMoney(cost, $"Automate : {liters} L ({pump.Label})");
            _ = new ExoSale
            {
                PumpId = pump.Id,
                PumpLabel = pump.Label,
                BizId = prepay.BizId,
                EmployeeName = "Automate",
                ClientName = prepay.ClientName,
                Litres = liters,
                Montant = cost,
                Date = Nova.UnixTimeNow(),
            }.Save();

            buyer?.Notify("Automate", $"{liters} L pour {Money(cost)}." + (refund >= 0.01 ? $" {Money(refund)} vous ont été rendus." : "") + " Bonne route !",
                NotificationManager.Type.Success, 7f);
            Log("🤖 Automate : plein terminé", "#3ddc84", $"{prepay.ClientName} s'est servi à l'automate.",
                ("Pompe", pump.Label), ("Litres", $"{liters} L"), ("Payé", Money(cost)), ("Rendu", Money(refund)),
                ("Paiement", prepay.Cash ? "Espèces" : "Carte"), ("Entreprise", prepay.BizId > 0 ? BizName(prepay.BizId) : "aucune"));
        }

        /// <summary>Menu affiché quand un joueur entre dans le point bleu de l'automate.</summary>
        public void AutomateMenu(Player player, string stationName)
        {
            if (player?.setup == null || !_loaded) return;

            // Station de l'automate : celle du modèle si une de ses pompes est proche, sinon la plus proche
            Vector3 pos = player.setup.transform.position;
            ExoPump nearest = Pumps.Where(p => Vector3.Distance(p.Position, pos) <= 100f)
                .OrderBy(p => p.StationName == stationName ? 0 : 1).ThenBy(p => Vector3.Distance(p.Position, pos)).FirstOrDefault();
            if (nearest == null)
            {
                player.Notify("Automate", "Aucune pompe configurée près de cet automate.", NotificationManager.Type.Error, 5f);
                return;
            }
            stationName = nearest.StationName;

            Prepay mine = _prepays.Values.FirstOrDefault(p => p.CharacterId == player.character.Id);
            if (mine != null)
            {
                AutomateCurrent(player, mine);
                return;
            }
            if (Config.AutomateDesactive)
            {
                player.Notify("Automate", "L'automate est hors service.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (StationStaffed(stationName))
            {
                player.Notify("Automate", "Un employé est en service : adressez-vous au personnel de la station.", NotificationManager.Type.Info, 6f);
                return;
            }

            List<ExoPump> pumps = Pumps.Where(p => p.StationName == stationName).OrderBy(p => p.PumpName).ToList();
            Panel panel = PanelHelper.Create($"Automate - {stationName}", UIPanel.PanelType.TabPrice, player, () => AutomateMenu(player, stationName));
            foreach (ExoPump pump in pumps)
            {
                string state = AutomatePumpState(pump, out bool usable);
                panel.AddTabLine($"{pump.PumpName} - {Money(PriceOf(pump))}/L", state, IconUtils.Others.None.Id, _ =>
                {
                    if (!usable)
                    {
                        player.Notify("Automate", $"{pump.PumpName} : {Nova.RemoveTextFormat(state)}.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    AutomateAmounts(player, pump);
                });
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private string AutomatePumpState(ExoPump pump, out bool usable)
        {
            usable = false;
            if (!pump.Ouverte) return Color("Fermée", Colors.Error);
            if (Config.StockActif && pump.Stock <= 0) return Color("Citerne vide", Colors.Error);
            if (_prepays.ContainsKey(pump.Id)) return Color("Occupée", Colors.Warning);
            usable = true;
            return Color("Libre", Colors.Success);
        }

        private void AutomateAmounts(Player player, ExoPump pump)
        {
            double price = PriceOf(pump);
            Panel panel = PanelHelper.Create($"Automate - {pump.PumpName}", UIPanel.PanelType.TabPrice, player, () => AutomateAmounts(player, pump));
            foreach (double amount in Config.MontantsList)
            {
                int liters = price > 0 ? (int)Math.Floor(amount / price) : 0;
                if (liters <= 0) continue;
                bool enough = !Config.StockActif || liters <= pump.Stock;
                panel.AddTabLine($"{amount:0.##} € d'essence", enough ? $"{liters} L" : Color("stock insuffisant", Colors.Grey), IconUtils.Others.None.Id, _ =>
                {
                    if (!enough)
                    {
                        player.Notify("Automate", "Pas assez de carburant dans la citerne pour ce montant.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    AutomatePay(player, pump, amount);
                });
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AutomatePay(Player player, ExoPump pump, double amount)
        {
            double price = PriceOf(pump);
            int liters = (int)Math.Floor(amount / price);
            Panel panel = PanelHelper.Create("Automate - Paiement", UIPanel.PanelType.Text, player, () => AutomatePay(player, pump, amount));
            panel.TextLines.Add($"{Color(pump.Label, Colors.Info)}");
            panel.TextLines.Add($"Montant : {Color(Money(amount), Colors.Warning)} ({liters} L à {Money(price)}/L)");
            panel.TextLines.Add($"La pompe est débloquée pendant {PrepayDuration / 60:0} minutes.");
            panel.TextLines.Add("Elle se bloque dès que le montant est atteint. Ce qui n'est pas utilisé vous est rendu.");
            panel.CloseButtonWithAction("Payer en espèces", () => Task.FromResult(DoPrepay(player, pump, amount, true)));
            panel.CloseButtonWithAction("Payer par carte", () => Task.FromResult(DoPrepay(player, pump, amount, false)));
            panel.PreviousButton();
            panel.Display();
        }

        private bool DoPrepay(Player player, ExoPump pump, double amount, bool cash)
        {
            if (StationStaffed(pump.StationName))
            {
                player.Notify("Automate", "Un employé vient de prendre son service : adressez-vous à lui.", NotificationManager.Type.Info, 6f);
                return true;
            }
            AutomatePumpState(pump, out bool usable);
            if (!usable || _prepays.Values.Any(p => p.CharacterId == player.character.Id))
            {
                player.Notify("Automate", "Cette pompe n'est plus disponible.", NotificationManager.Type.Error, 5f);
                return true;
            }
            double price = PriceOf(pump);
            int liters = (int)Math.Floor(amount / price);
            if (liters <= 0 || (Config.StockActif && liters > pump.Stock))
            {
                player.Notify("Automate", "Pas assez de carburant dans la citerne pour ce montant.", NotificationManager.Type.Error, 5f);
                return true;
            }
            if ((cash ? player.Money : player.Bank) < amount)
            {
                player.Notify("Automate", cash ? "Vous n'avez pas assez d'espèces." : "Vous n'avez pas assez d'argent en banque.", NotificationManager.Type.Error, 5f);
                return false;
            }

            if (cash) player.AddMoney(-amount, "Automate essence");
            else player.AddBankMoney(-amount, "Automate essence");

            Prepay prepay = new Prepay
            {
                PumpId = pump.Id,
                PumpLabel = pump.Label,
                CharacterId = player.character.Id,
                ClientName = player.FullName,
                Amount = amount,
                PricePerLiter = price,
                MaxLiters = liters,
                Cash = cash,
                BizId = pump.BizId != 0 ? pump.BizId : Config.BizIdList.FirstOrDefault(),
                Expires = Time.realtimeSinceStartup + PrepayDuration,
            };
            _prepays[pump.Id] = prepay;

            player.Notify("Automate", $"{pump.PumpName} débloquée : {liters} L maximum. Branchez le pistolet sur votre véhicule.", NotificationManager.Type.Success, 8f);
            player.setup.TargetSetGPSTarget(pump.Position);
            Log("🤖 Automate : paiement", "#3fd2ff", $"{player.FullName} a payé à l'automate.",
                ("Pompe", pump.Label), ("Montant", Money(amount)), ("Litres max", $"{liters} L"), ("Paiement", cash ? "Espèces" : "Carte"));
            return true;
        }

        /// <summary>Le joueur a déjà payé : rappel et annulation possible.</summary>
        private void AutomateCurrent(Player player, Prepay prepay)
        {
            int left = Mathf.Max(0, Mathf.CeilToInt(prepay.Expires - Time.realtimeSinceStartup));
            Panel panel = PanelHelper.Create("Automate - Votre paiement", UIPanel.PanelType.Text, player, () => AutomateCurrent(player, prepay));
            panel.TextLines.Add($"{Color(prepay.PumpLabel, Colors.Info)} est débloquée pour vous.");
            panel.TextLines.Add($"Payé : {Money(prepay.Amount)} ({prepay.MaxLiters} L maximum)");
            panel.TextLines.Add(prepay.InUse ? "Remplissage en cours…" : $"Temps restant : {left / 60} min {left % 60:00} s");
            if (!prepay.InUse)
                panel.CloseButtonWithAction(Color("Annuler et être remboursé", Colors.Error), () =>
                {
                    if (_prepays.TryGetValue(prepay.PumpId, out Prepay current) && current == prepay && !prepay.InUse)
                    {
                        _prepays.Remove(prepay.PumpId);
                        RefundPrepay(prepay, player, prepay.Amount);
                        player.Notify("Automate", $"Paiement annulé, {Money(prepay.Amount)} remboursés.", NotificationManager.Type.Info, 5f);
                        Log("↩️ Automate : annulation", "#ffc93c", $"{prepay.ClientName} a annulé son paiement.",
                            ("Pompe", prepay.PumpLabel), ("Remboursé", Money(prepay.Amount)));
                    }
                    return Task.FromResult(true);
                });
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Automate : gestion staff (AAMenu > Points bleus > Automate essence)
        // ------------------------------------------------------------------

        public async void AutomateAdmin(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<ExoAutomatePattern> patterns = await ExoAutomatePattern.QueryAll() ?? new List<ExoAutomatePattern>();

            Panel panel = PanelHelper.Create("Automates - Placer un point", UIPanel.PanelType.Tab, player, () => AutomateAdmin(player));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun automate : créez-en un", _ => { });
            foreach (ExoAutomatePattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(ExoAutomatePattern);
                    pattern.Context = this;
                    if (await PointHelper.CreateNPoint(player, pattern))
                        player.Notify(Title, $"Point bleu « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                });
            }
            if (patterns.Count > 0)
                panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouvel automate", () => AutomateNew(player));
            panel.NextButton("Automates", async () => await AutomateModels(player));
            panel.NextButton("Points placés", async () => await AutomatePoints(player));
            panel.CloseButton();
            panel.Display();
        }

        public void AutomateNew(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<string> stations = Pumps.Select(p => p.StationName).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToList();

            Panel panel = PanelHelper.Create("Nouvel automate - Station", UIPanel.PanelType.Tab, player, () => AutomateNew(player));
            if (stations.Count == 0)
                panel.AddTabLine("Configurez d'abord des pompes", _ => { });
            foreach (string station in stations)
            {
                panel.AddTabLine(station, async _ =>
                {
                    ExoAutomatePattern pattern = new ExoAutomatePattern { PatternName = $"Automate {station}", StationName = station };
                    if (await pattern.Save())
                    {
                        player.Notify(Title, $"« {pattern.PatternName} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 6f);
                        panel.Previous();
                    }
                });
            }
            panel.AddButton("Créer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task AutomateModels(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<ExoAutomatePattern> patterns = await ExoAutomatePattern.QueryAll() ?? new List<ExoAutomatePattern>();

            Panel panel = PanelHelper.Create("Automates", UIPanel.PanelType.Tab, player, async () => await AutomateModels(player));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun automate", _ => { });
            foreach (ExoAutomatePattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(ExoAutomatePattern);
                    pattern.Context = this;
                    await PointHelper.DeleteNPointsByPattern(player, pattern);
                    if (await pattern.Delete())
                        player.Notify(Title, $"« {pattern.PatternName} » et ses points supprimés (visible après reconnexion).", NotificationManager.Type.Success, 6f);
                    panel.Refresh();
                });
            }
            if (patterns.Count > 0)
                panel.AddButton(Color("Supprimer", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task AutomatePoints(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<NPoint> points = await NPoint.Query(p => p.TypeName == nameof(ExoAutomatePattern)) ?? new List<NPoint>();
            Dictionary<int, string> names = (await ExoAutomatePattern.QueryAll() ?? new List<ExoAutomatePattern>()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = PanelHelper.Create("Automates - Points placés", UIPanel.PanelType.Tab, player, async () => await AutomatePoints(player));
            if (points.Count == 0)
                panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await PointHelper.SetNPointPosition(player, point))
                                player.Notify(Title, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await PointHelper.DeleteNPoint(point);
                            player.Notify(Title, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }
            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Webhook Discord
        // ==================================================================

        private bool HasWebhook => Config != null && !string.IsNullOrWhiteSpace(Config.WebhookUrl) && Config.WebhookUrl.StartsWith("http");

        private void Log(string title, string color, string description, params (string name, string value)[] fields)
        {
            if (!HasWebhook) return;
            _ = SendWebhook(Config.WebhookUrl, title, color, description, fields);
        }

        private static async Task SendWebhook(string url, string title, string color, string description, (string name, string value)[] fields)
        {
            try
            {
                List<string> names = fields.Select(f => string.IsNullOrEmpty(f.name) ? "-" : f.name).ToList();
                List<string> values = fields.Select(f => string.IsNullOrEmpty(f.value) ? "-" : Nova.RemoveTextFormat(f.value)).ToList();
                await new DiscordWebhookClient(url).SendEmbedAsync(color, title, string.IsNullOrEmpty(description) ? "-" : description,
                    names, values, true, true, $"{Title} - {DateTime.Now:dd/MM/yyyy HH:mm}");
            }
            catch (Exception ex)
            {
                Logger.LogError(Title, "Webhook Discord : " + ex.Message);
            }
        }

        // ==================================================================
        //  Outils
        // ==================================================================

        public double PriceOf(ExoPump pump) => pump != null && pump.PrixLitre > 0 ? pump.PrixLitre : Config.PrixLitre;

        public bool IsAllowedBiz(int bizId) => bizId > 0 && Config.BizIdList.Contains(bizId);

        public bool IsAllowedAt(Player player, ExoPump pump)
        {
            if (player == null || !player.HasBiz) return false;
            int bizId = player.character.BizId;
            return IsAllowedBiz(bizId) && (pump.BizId == 0 || pump.BizId == bizId);
        }

        private string AllowedBizNames(ExoPump pump)
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
            List<ExoPump> pumps = Pumps.Where(p => p.BizId == 0 || p.BizId == bizId).ToList();

            bool onDuty = IsOnDuty(player);
            int colleagues = Nova.server.GetAllInGamePlayers().Count(p => p?.character != null && p.character.BizId == bizId && IsOnDuty(p));

            Panel panel = PanelHelper.Create($"Station-service - {biz?.BizName}", UIPanel.PanelType.Tab, player, () => EmployeeMenu(player));
            panel.AddTabLine(onDuty ? Color("Terminer mon service", Colors.Error) : Color("Prendre mon service", Colors.Success), _ =>
            {
                SetDuty(player, !onDuty);
                panel.Refresh();
            });
            panel.AddTabLine($"En service dans l'entreprise : {colleagues}", _ => { });
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
            List<ExoPump> pumps = Pumps.Where(p => p.BizId == 0 || p.BizId == bizId).OrderBy(p => p.StationName).ThenBy(p => p.PumpName).ToList();

            Panel panel = PanelHelper.Create("Station-service - Pompes", UIPanel.PanelType.TabPrice, player, () => EmployeePumps(player));
            if (pumps.Count == 0)
                panel.AddTabLine("Aucune pompe attribuée", _ => { });
            foreach (ExoPump pump in pumps)
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

        private void EmployeePump(Player player, ExoPump pump)
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

        private void OrderFuel(Player player, ExoPump pump)
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
                    Log("🛢️ Commande de carburant", "#3fd2ff", $"{player.FullName} a commandé du carburant.",
                        ("Pompe", pump.Label), ("Litres", $"{liters} L"), ("Coût", Money(cost)), ("Entreprise", BizName(biz.Id)), ("Citerne", $"{pump.Stock} / {Config.CapaciteCiterne} L"));
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
                Log("🎁 Plein offert", "#ffc93c", $"{employee.FullName} a offert un plein.",
                    ("Pompe", bill.Pump.Label), ("Litres", $"{bill.Liters} L"), ("Valeur", Money(bill.Total)));
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
                    Log("❌ Facture refusée", "#ff4d5e", $"{client.FullName} a refusé de payer.",
                        ("Pompe", bill.Pump.Label), ("Montant", Money(bill.Total)), ("Employé", employeeName));
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
            _ = new ExoSale
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
            Log("⛽ Vente de carburant", "#3ddc84", $"{client.FullName} a payé son plein.",
                ("Pompe", bill.Pump.Label), ("Litres", $"{bill.Liters} L"), ("Total", Money(total)),
                ("Paiement", cash ? "Espèces" : "Carte"), ("Employé", employee?.FullName ?? "?"), ("Prime", Money(commission)),
                ("Entreprise", BizName(bill.BizId)));
            return true;
        }

        private void RemoveBill(int employeeId, PendingBill bill)
        {
            if (_bills.TryGetValue(employeeId, out List<PendingBill> list))
                list.Remove(bill);
        }

        private async Task SalesMenu(Player player, int bizId)
        {
            List<ExoSale> sales = bizId > 0 ? await ExoSale.Query(s => s.BizId == bizId) : await ExoSale.QueryAll();
            sales = (sales ?? new List<ExoSale>()).OrderByDescending(s => s.Date).Take(30).ToList();

            Panel panel = PanelHelper.Create("Ventes récentes", UIPanel.PanelType.TabPrice, player, async () => await SalesMenu(player, bizId));
            if (sales.Count == 0)
                panel.AddTabLine("Aucune vente", _ => { });
            foreach (ExoSale sale in sales)
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
                    Log("⚙️ Configuration", "#ffb21e", $"{player.FullName} a changé le prix du litre.", ("Nouveau prix", Money(Config.PrixLitre)));
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
            panel.AddTabLine($"Automate (sans employé en service) : {OnOff(!Config.AutomateDesactive)}", async _ =>
            {
                Config.AutomateDesactive = !Config.AutomateDesactive;
                await Config.Save();
                panel.Refresh();
            });
            panel.AddTabLine($"Montants de l'automate : {string.Join(", ", Config.MontantsList)} €", _ =>
                AskInput(player, "Montants de l'automate", "Montants proposés à l'automate, séparés par des virgules", Config.Montants ?? "10,20,30,40,50,60,70,80", async input =>
                {
                    List<double> list = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => TryParseNumber(x, out double v) ? v : 0).Where(v => v > 0).Distinct().OrderBy(v => v).ToList();
                    if (list.Count == 0) return false;
                    Config.Montants = string.Join(",", list.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                    return await Config.Save();
                }));
            panel.AddTabLine("Automates (points bleus)", _ => AutomateAdmin(player));
            panel.AddTabLine($"Webhook Discord : {(HasWebhook ? Color("configuré", Colors.Success) : Color("non configuré", Colors.Grey))}", _ =>
                AskInput(player, "Webhook Discord", "Collez l'URL du webhook Discord (ou « 0 » pour le retirer).\nAstuce : la commande /exowebhook <url> marche aussi.", "https://discord.com/api/webhooks/...", async input =>
                {
                    Config.WebhookUrl = input == "0" ? "" : input;
                    await Config.Save();
                    player.Notify(Title, HasWebhook ? "Webhook enregistré." : "Webhook retiré.", NotificationManager.Type.Success, 4f);
                    return true;
                }));
            if (HasWebhook)
                panel.AddTabLine("Tester le webhook", _ =>
                {
                    Log("✅ Test du webhook", "#3fd2ff", $"Test envoyé par {player.FullName}.", ("Pompes", Pumps.Count.ToString()), ("Prix du litre", Money(Config.PrixLitre)));
                    player.Notify(Title, "Message de test envoyé sur Discord.", NotificationManager.Type.Info, 4f);
                });
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
                    await RefreshBizLines();
                    Log("⚙️ Configuration", "#ffb21e", $"{player.FullName} a {(allowed ? "retiré" : "autorisé")} une entreprise.", ("Entreprise", $"[{biz.Id}] {biz.BizName}"));
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
            List<ExoPump> pumps = Pumps.OrderBy(p => p.StationName).ThenBy(p => p.PumpName).ToList();

            Panel panel = PanelHelper.Create("Pompes configurées", UIPanel.PanelType.TabPrice, player, () => AdminPumps(player));
            if (pumps.Count == 0)
                panel.AddTabLine("Aucune pompe : utilisez « Ajouter »", _ => { });
            foreach (ExoPump pump in pumps)
            {
                string owner = pump.BizId == 0 ? "Toutes" : BizName(pump.BizId);
                panel.AddTabLine($"{pump.Label} ({owner})", Money(PriceOf(pump)), IconUtils.Others.None.Id, _ => AdminPump(player, pump));
            }
            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminPump(Player player, ExoPump pump)
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

        private void AdminPumpBiz(Player player, ExoPump pump)
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

        private async Task SetPumpBiz(Player player, ExoPump pump, int bizId, Panel panel)
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
            ExoPump existing = Pumps.FirstOrDefault(p => Vector3.Distance(p.Position, pos) <= LinkRange);
            if (existing != null)
            {
                player.Notify(Title, $"Cette pompe est déjà configurée : {existing.Label}.", NotificationManager.Type.Warning, 5f);
                AdminPump(player, existing);
                return;
            }

            // Reprend le nom de la station la plus proche s'il y en a une
            ExoPump neighbour = Pumps.Where(p => Vector3.Distance(p.Position, pos) <= StationClusterRange)
                .OrderBy(p => Vector3.Distance(p.Position, pos)).FirstOrDefault();
            string stationName = neighbour?.StationName ?? $"Station {Pumps.Select(p => p.StationName).Distinct().Count() + 1}";
            int number = Pumps.Count(p => p.StationName == stationName) + 1;

            AskInput(player, "Nouvelle pompe", $"Station : {stationName}\nNom de la pompe :", $"Pompe {number}", async input =>
            {
                ExoPump pump = new ExoPump
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
                ExoPump neighbour = Pumps.Where(p => Vector3.Distance(p.Position, pos) <= StationClusterRange)
                    .OrderBy(p => Vector3.Distance(p.Position, pos)).FirstOrDefault();
                string stationName = neighbour?.StationName ?? $"Station {Pumps.Select(p => p.StationName).Distinct().Count() + 1}";

                ExoPump pump = new ExoPump
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
        public ExoPump Pump;
        public int EmployeeCharacterId;
        public int BizId;
        public uint VehicleNetId;
        public bool IsElectric;
        public int Liters;
        /// <summary>Non null quand la pompe a été payée à l'automate.</summary>
        public Prepay Prepay;
        /// <summary>Litres maximum (automate), 0 = pas de limite.</summary>
        public int MaxLiters;
    }

    /// <summary>Paiement d'avance à l'automate : débloque une pompe pour un joueur.</summary>
    internal class Prepay
    {
        public int PumpId;
        public string PumpLabel;
        public int CharacterId;
        public string ClientName;
        public double Amount;
        public double PricePerLiter;
        public int MaxLiters;
        public bool Cash;
        public int BizId;
        public float Expires;
        public bool InUse;
    }

    /// <summary>Plein terminé, en attente de paiement par un client.</summary>
    internal class PendingBill
    {
        public ExoPump Pump;
        public int BizId;
        public int Liters;
        public double PricePerLiter;
        public int ClientCharacterId;
        public double Total => Math.Round(Liters * PricePerLiter, 2);
    }

    // ======================================================================
    //  Données sauvegardées (base ModKit)
    // ======================================================================

    [Table("GazConfig")]
    public class ExoConfig : ModEntity<ExoConfig>
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
        public string WebhookUrl { get; set; } = "";
        /// <summary>Faux par défaut, y compris pour les anciennes bases (colonne ajoutée à 0).</summary>
        public bool AutomateDesactive { get; set; }
        /// <summary>Montants proposés par l'automate, séparés par des virgules.</summary>
        public string Montants { get; set; } = "10,20,30,40,50,60,70,80";

        [Ignore]
        public List<double> MontantsList
        {
            get
            {
                List<double> list = (Montants ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0)
                    .Where(v => v > 0).ToList();
                return list.Count > 0 ? list : new List<double> { 10, 20, 30, 40, 50, 60, 70, 80 };
            }
        }

        [Ignore]
        public List<int> BizIdList
        {
            get => (BizIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out int id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            set => BizIds = string.Join(",", value.Distinct());
        }
    }

    [Table("GazPump")]
    public class ExoPump : ModEntity<ExoPump>
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

    [Table("GazSale")]
    public class ExoSale : ModEntity<ExoSale>
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

    /// <summary>
    /// Point bleu « Automate essence » : placé par le staff dans une station, il permet de payer
    /// d'avance quand aucun employé n'est en service.
    /// </summary>
    public class ExoAutomatePattern : ModEntity<ExoAutomatePattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }
        public string StationName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(ExoAutomatePattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public void OnPlayerTrigger(Player player) => ExoPlugin.Instance?.AutomateMenu(player, StationName);

        public async Task SetProperties(int id)
        {
            ExoAutomatePattern result = await Query(id);
            Id = id;
            TypeName = nameof(ExoAutomatePattern);
            PatternName = result?.PatternName;
            StationName = result?.StationName;
        }

        public void CreateOrGenerate(Player player) => ExoPlugin.Instance?.AutomateAdmin(player);

        public void SetPatternData(Player player) => ExoPlugin.Instance?.AutomateNew(player);

        public Task GetPatternData(Player player, bool forEdit) => ExoPlugin.Instance?.AutomateModels(player) ?? Task.CompletedTask;

        public Task GetNPoints(Player player) => ExoPlugin.Instance?.AutomatePoints(player) ?? Task.CompletedTask;
    }
}
