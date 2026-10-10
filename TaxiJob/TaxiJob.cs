using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using UnityEngine;
using Logger = ModKit.Internal.Logger;

namespace TaxiJob
{
    /// <summary>
    /// Job taxi complet :
    /// - courses « PNJ » en solo (comme un farm) entre des points placés par le staff,
    ///   entièrement en voiture (points orange, uniquement déclenchés au volant) ;
    /// - appels de vrais joueurs (/appeltaxi) que les chauffeurs en service acceptent ;
    /// - tarif au kilomètre, pourboire si la course est rapide, statistiques et classement ;
    /// - tout se configure en jeu par le staff (AAMenu > Points bleus > Taxi, ou /taxiadmin).
    /// </summary>
    public class TaxiJobPlugin : ModKit.ModKit
    {
        public static TaxiJobPlugin Instance { get; private set; }

        private const string Title = "Taxi";
        public const string Author = "Matheo Mercier";

        /// <summary>Activité d'entreprise sur laquelle AAMenu affiche les lignes taxi (même valeur que l'ancien plugin).</summary>
        private static readonly Activity.Type TaxiActivity = (Activity.Type)6;

        private TaxiSettings _settings = new TaxiSettings();
        private List<TaxiPoint> _points = new List<TaxiPoint>();

        private readonly HashSet<uint> _onDuty = new HashSet<uint>();
        private readonly Dictionary<uint, Ride> _rides = new Dictionary<uint, Ride>();
        private readonly Dictionary<uint, List<DateTime>> _rideHistory = new Dictionary<uint, List<DateTime>>();
        private readonly List<TaxiCall> _calls = new List<TaxiCall>();
        private readonly System.Random _rng = new System.Random();
        private int _nextCallId = 1;

        public TaxiJobPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "2.1.0", Author);
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            Orm.RegisterTable<TaxiSettings>();
            Orm.RegisterTable<TaxiPoint>();
            Orm.RegisterTable<TaxiDriverStats>();
            Orm.RegisterTable<TaxiStation>();

            // Centrales taxi (points bleus) + entrée staff dans AAMenu > Points bleus
            TaxiStation pattern = new TaxiStation(false) { Context = this };
            PointHelper.AddPattern(nameof(TaxiStation), pattern);
            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Taxi", pattern, this);
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "Taxi - Configuration du job",
                    (Action<UIPanel>)(ui => OpenAdminMenu(PanelHelper.ReturnPlayerFromPanel(ui))));
                InsertBizMenu();
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /taxi et /taxiadmin.");
            }

            RegisterCommands();
            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            TaxiTicker.Start(this);
            _ = LoadDataAsync();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version} by {Author}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            ResetPlayer(player.netId);
            PointHelper.InitAllNPoint(player);
        }

        // ==================================================================
        //  Données
        // ==================================================================

        private async Task LoadDataAsync()
        {
            try
            {
                List<TaxiSettings> all = await TaxiSettings.QueryAll();
                _settings = all.FirstOrDefault();
                if (_settings == null)
                {
                    _settings = new TaxiSettings();
                    await ImportOldConfig();
                    await _settings.Save();
                }
                await ReloadPoints();
                Logger.LogSuccess(PluginInformations.SourceName, $"{_points.Count} point(s) de course chargé(s)");
            }
            catch (Exception e)
            {
                Logger.LogWarning(PluginInformations.SourceName, $"Erreur au chargement des données : {e.Message}");
            }
        }

        private async Task ReloadPoints()
        {
            _points = await TaxiPoint.QueryAll();
        }

        /// <summary>Reprend la config de l'ancien plugin (jobtaxibymatheo : TaxiJob/config.json) si elle existe.</summary>
        private async Task ImportOldConfig()
        {
            string path = Path.Combine(pluginsPath, "TaxiJob", "config.json");
            if (!File.Exists(path)) return;

            try
            {
                OldConfig old = JsonConvert.DeserializeObject<OldConfig>(File.ReadAllText(path));
                if (old == null) return;

                _settings.BizId = old.BizId;
                _settings.MinPay = old.MinPay;
                _settings.MaxPay = old.MaxPay;

                int index = 1;
                if (old.StartPoint != null && (old.StartPoint.X != 0 || old.StartPoint.Y != 0 || old.StartPoint.Z != 0))
                    await new TaxiPoint { Name = "Départ", X = old.StartPoint.X, Y = old.StartPoint.Y, Z = old.StartPoint.Z, IsPickup = true, IsDropoff = false, Enabled = true }.Save();
                foreach (OldPos pos in old.Points ?? new List<OldPos>())
                    await new TaxiPoint { Name = $"Arrêt {index++}", X = pos.X, Y = pos.Y, Z = pos.Z, IsPickup = true, IsDropoff = true, Enabled = true }.Save();

                File.Move(path, path + ".importe");
                Logger.LogSuccess(PluginInformations.SourceName, "Ancienne config jobtaxibymatheo importée");
            }
            catch (Exception e)
            {
                Logger.LogWarning(PluginInformations.SourceName, $"Import de l'ancienne config impossible : {e.Message}");
            }
        }

        private async Task<bool> SaveSettings(Player player)
        {
            if (await _settings.Save()) return true;
            Notify(player, "Erreur lors de l'enregistrement des paramètres.", NotificationManager.Type.Error);
            return false;
        }

        // ==================================================================
        //  Menus AAMenu (entreprise) et commandes
        // ==================================================================

        private void InsertBizMenu()
        {
            List<Activity.Type> activities = new List<Activity.Type> { TaxiActivity };
            AAMenu.Menu.AddBizTabLine(PluginInformations, activities, null, "Taxi - Menu chauffeur",
                (Action<UIPanel>)(ui => OpenDriverMenu(GetPlayer(ui.playerId))));
            AAMenu.Menu.AddBizTabLine(PluginInformations, activities, null, "Taxi - Prendre / quitter le service",
                (Action<UIPanel>)(ui => ToggleService(GetPlayer(ui.playerId))));
            AAMenu.Menu.AddBizTabLine(PluginInformations, activities, null, "Taxi - Commencer une course",
                (Action<UIPanel>)(ui => StartSoloRide(GetPlayer(ui.playerId), true)));
        }

        private void RegisterCommands()
        {
            new SChatCommand("/taxi", "Menu chauffeur de taxi", "/taxi [service|course|annuler]",
                (Action<Player, string[]>)((player, args) =>
                {
                    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    switch (sub)
                    {
                        case "service": ToggleService(player); break;
                        case "course": StartSoloRide(player, true); break;
                        case "annuler": CancelRide(player, true); break;
                        default: OpenDriverMenu(player); break;
                    }
                })).Register();

            new SChatCommand("/appeltaxi", "Appeler un taxi à votre position", "/appeltaxi [message|annuler]",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (args.Length == 1 && args[0].ToLowerInvariant() == "annuler")
                        CancelCall(player);
                    else
                        CallTaxi(player, string.Join(" ", args));
                })).Register();

            new SChatCommand("/taxiadmin", "Configurer le job taxi (staff)", "/taxiadmin",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdmin || !player.serviceAdmin)
                    {
                        Notify(player, "Vous devez être staff et en service admin.", NotificationManager.Type.Error);
                        return;
                    }
                    OpenAdminMenu(player);
                })).Register();
        }

        // ==================================================================
        //  Chauffeur
        // ==================================================================

        /// <summary>Le joueur peut-il faire le job ? (BizId = 0 : ouvert à tous, comme un farm)</summary>
        private bool IsTaxi(Player p)
        {
            if (p == null || p.character == null) return false;
            return _settings.BizId <= 0 || p.character.BizId == _settings.BizId;
        }

        private bool IsOnDuty(Player p) => !_settings.RequireService || _onDuty.Contains(p.netId);

        private bool CanWork(Player p)
        {
            if (p == null) return false;
            if (!IsTaxi(p))
            {
                Notify(p, "Vous n'êtes pas employé de la compagnie de taxi.", NotificationManager.Type.Error);
                return false;
            }
            if (!IsOnDuty(p))
            {
                Notify(p, "Prenez d'abord votre service.", NotificationManager.Type.Warning);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Véhicule dont le joueur est le conducteur (siège 0), ou null.
        /// Même source que le serveur du jeu : CharacterDriver.currentVehicle.
        /// </summary>
        private static Life.VehicleSystem.Vehicle DrivenVehicle(Player p)
        {
            if (p?.setup == null) return null;
            uint vehicleId = p.GetVehicleId();
            if (vehicleId == 0 || !NetworkServer.spawned.TryGetValue(vehicleId, out NetworkIdentity identity) || identity == null) return null;

            Life.VehicleSystem.Vehicle vehicle = identity.GetComponent<Life.VehicleSystem.Vehicle>();
            if (vehicle == null || vehicle.netSeats == null || vehicle.netSeats.Count == 0) return null;
            return vehicle.netSeats[0].passengerId == p.setup.netId ? vehicle : null;
        }

        /// <summary>Modèle du véhicule conduit par le joueur, -1 s'il n'est pas au volant, -2 si modèle inconnu.</summary>
        private static int DrivenModelId(Player p)
        {
            Life.VehicleSystem.Vehicle vehicle = DrivenVehicle(p);
            if (vehicle == null) return -1;
            return Nova.v.GetVehicle(vehicle.VehicleDbId)?.modelId ?? -2;
        }

        /// <summary>Raison pour laquelle le véhicule du joueur ne convient pas, ou null s'il convient.</summary>
        private string VehicleProblem(Player p)
        {
            List<int> allowed = _settings.AllowedModelIds;
            bool needBizVehicle = _settings.RequireBizVehicle && _settings.BizId > 0;
            if (!_settings.RequireVehicle && allowed.Count == 0 && !needBizVehicle) return null;

            Life.VehicleSystem.Vehicle vehicle = DrivenVehicle(p);
            if (vehicle == null) return "Vous devez être au volant d'un véhicule.";
            if (needBizVehicle && vehicle.bizId != _settings.BizId) return "Ce véhicule n'appartient pas à la compagnie de taxi.";
            if (allowed.Count > 0 && !allowed.Contains(DrivenModelId(p))) return "Ce véhicule n'est pas un taxi autorisé.";
            return null;
        }

        /// <summary>Vérifie que le joueur conduit un véhicule autorisé (si la config l'exige).</summary>
        private bool CheckVehicle(Player p)
        {
            string problem = VehicleProblem(p);
            if (problem == null) return true;
            Notify(p, problem, NotificationManager.Type.Warning);
            return false;
        }

        public void OpenDriverMenu(Player player)
        {
            if (player == null) return;
            if (!IsTaxi(player))
            {
                Notify(player, "Vous n'êtes pas employé de la compagnie de taxi. Pour appeler un taxi : /appeltaxi", NotificationManager.Type.Info);
                return;
            }

            _rides.TryGetValue(player.netId, out Ride ride);
            int pending = PendingCalls().Count;

            Panel panel = PanelHelper.Create("Taxi - Menu chauffeur", UIPanel.PanelType.Tab, player, () => OpenDriverMenu(player));
            panel.TextLines.Add(Col($"By {Author}", Gold));

            if (_settings.RequireService)
            {
                bool duty = _onDuty.Contains(player.netId);
                panel.AddTabLine(duty ? $"Service : {Col("EN SERVICE", Green)} (quitter)" : $"Service : {Col("HORS SERVICE", Red)} (prendre)", _ =>
                {
                    ToggleService(player);
                    panel.Refresh();
                });
            }

            if (ride == null)
            {
                panel.AddTabLine("Commencer une course (client PNJ)", _ => StartSoloRide(player, true));
                panel.AddTabLine($"Appels clients en attente ({pending})", _ => CallsMenu(player));
            }
            else
            {
                panel.AddTabLine($"Course en cours : {Col(RideDescription(ride), Gold)}", _ => { });
                panel.AddTabLine(Col("Annuler la course en cours", Red), _ =>
                {
                    CancelRide(player, true);
                    panel.Refresh();
                });
            }

            panel.AddTabLine($"Enchaîner les courses automatiquement : {YesNo(_settings.AutoChain)}", _ => { });
            panel.AddTabLine("Mes statistiques", async _ => await MyStatsMenu(player));
            panel.AddTabLine("Classement des chauffeurs", async _ => await LeaderboardMenu(player, false));

            panel.NextButton("Valider", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private void ToggleService(Player p)
        {
            if (p == null) return;
            if (!IsTaxi(p))
            {
                Notify(p, "Vous n'êtes pas employé de la compagnie de taxi.", NotificationManager.Type.Error);
                return;
            }
            if (!_settings.RequireService)
            {
                Notify(p, "La prise de service n'est pas nécessaire sur ce serveur.", NotificationManager.Type.Info);
                return;
            }

            if (_onDuty.Remove(p.netId))
            {
                CancelRide(p, false);
                Notify(p, "Fin de service. Bonne journée !", NotificationManager.Type.Info);
            }
            else
            {
                _onDuty.Add(p.netId);
                int pending = PendingCalls().Count;
                Notify(p, pending > 0 ? $"Service pris. {pending} appel(s) client en attente (/taxi)." : "Service pris. Montez dans votre taxi !", NotificationManager.Type.Success);
            }
        }

        private void CallsMenu(Player player)
        {
            List<TaxiCall> calls = PendingCalls();
            Panel panel = PanelHelper.Create("Taxi - Appels clients", UIPanel.PanelType.Tab, player, () => CallsMenu(player));

            if (calls.Count == 0) panel.AddTabLine("Aucun appel en attente", _ => { });
            foreach (TaxiCall call in calls)
            {
                int minutes = (int)(DateTime.Now - call.CreatedAt).TotalMinutes;
                int meters = (int)Vector3.Distance(Position(player), call.Position);
                string message = string.IsNullOrWhiteSpace(call.Message) ? "" : $" « {call.Message} »";
                panel.AddTabLine($"{call.CallerName} - {meters} m - il y a {minutes} min{message}", _ => AcceptCall(player, call));
            }

            if (calls.Count > 0) panel.NextButton("Accepter", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Courses PNJ (solo)
        // ==================================================================

        private void StartSoloRide(Player p, bool verbose)
        {
            if (!CanWork(p)) return;
            if (_rides.ContainsKey(p.netId))
            {
                if (verbose) Notify(p, "Vous avez déjà une course en cours (/taxi annuler pour l'abandonner).", NotificationManager.Type.Warning);
                return;
            }
            if (_settings.MaxRidesPerHour > 0 && RidesLastHour(p.netId) >= _settings.MaxRidesPerHour)
            {
                Notify(p, $"Vous avez atteint la limite de {_settings.MaxRidesPerHour} courses par heure. Faites une pause !", NotificationManager.Type.Warning);
                return;
            }
            if (!CheckVehicle(p)) return;

            List<TaxiPoint> pickups = _points.Where(x => x.Enabled && x.IsPickup).ToList();
            if (pickups.Count == 0 || !_points.Any(x => x.Enabled && x.IsDropoff && pickups.Any(y => y.Id != x.Id)))
            {
                Notify(p, "Aucune course disponible : le staff doit configurer des points de course.", NotificationManager.Type.Error);
                return;
            }

            // On évite de donner un client là où le chauffeur se trouve déjà
            Vector3 here = Position(p);
            List<TaxiPoint> far = pickups.Where(x => Vector3.Distance(here, x.Position) > 50f).ToList();
            TaxiPoint pickup = Pick(far.Count > 0 ? far : pickups);

            Ride ride = new Ride { DriverId = p.netId, Stage = RideStage.ToPickup, Pickup = pickup, StageStart = DateTime.Now };
            _rides[p.netId] = ride;
            SetRideCheckpoint(p, ride, pickup.Position, OnPickupReached);

            int meters = (int)Vector3.Distance(here, pickup.Position);
            Notify(p, $"Nouveau client : {pickup.Name} ({meters} m). Rejoignez le point orange en voiture.", NotificationManager.Type.Info);
        }

        private void OnPickupReached(Player p, Ride ride)
        {
            List<TaxiPoint> drops = _points.Where(x => x.Enabled && x.IsDropoff && x.Id != ride.Pickup.Id).ToList();
            if (drops.Count == 0)
            {
                _rides.Remove(p.netId);
                Notify(p, "Plus aucune destination disponible, course annulée.", NotificationManager.Type.Error);
                return;
            }

            Vector3 from = ride.Pickup.Position;
            List<TaxiPoint> farEnough = drops.Where(x => Vector3.Distance(from, x.Position) >= _settings.MinRideDistance).ToList();
            // Si aucune destination n'est assez loin, on prend la plus éloignée
            TaxiPoint drop = farEnough.Count > 0 ? Pick(farEnough) : drops.OrderByDescending(x => Vector3.Distance(from, x.Position)).First();

            ride.Dropoff = drop;
            ride.Distance = Vector3.Distance(from, drop.Position);
            ride.Fare = ComputeFare(ride.Distance);
            ride.TimeLimit = ComputeTimeLimit(ride.Distance);
            ride.Stage = RideStage.ToDropoff;
            ride.StageStart = DateTime.Now;
            SetRideCheckpoint(p, ride, drop.Position, OnDropoffReached);

            Notify(p, $"Client à bord ! Direction {drop.Name} ({(int)ride.Distance} m). Tarif : {ride.Fare}€. " +
                      $"Pourboire si vous arrivez en moins de {FormatDuration(ride.TimeLimit)}.", NotificationManager.Type.Success);
        }

        private void OnDropoffReached(Player p, Ride ride)
        {
            _rides.Remove(p.netId);

            double elapsed = (DateTime.Now - ride.StageStart).TotalSeconds;
            int tip = elapsed <= ride.TimeLimit ? ride.Fare * _settings.TipPercent / 100 : 0;
            int total = ride.Fare + tip;

            p.AddMoney(total, "Course taxi");
            AddRideToHistory(p.netId);
            _ = AddStats(p, total, ride.Distance);

            string detail = tip > 0 ? $"{ride.Fare}€ + {tip}€ de pourboire" : $"{ride.Fare}€ (pas de pourboire, trop lent)";
            Notify(p, $"Course terminée en {FormatDuration(elapsed)} : +{total}€ ({detail}).", NotificationManager.Type.Success);

            if (_settings.AutoChain)
                StartSoloRide(p, false);
            else
                Notify(p, "Nouvelle course : /taxi course ou le menu taxi.", NotificationManager.Type.Info);
        }

        private int ComputeFare(float meters)
        {
            double fare = _settings.BasePay + _settings.PayPerKm * meters / 1000.0;
            if (_settings.MinPay > 0) fare = Math.Max(fare, _settings.MinPay);
            if (_settings.MaxPay > 0) fare = Math.Min(fare, _settings.MaxPay);
            return (int)Math.Round(fare);
        }

        /// <summary>Temps (s) pour toucher le pourboire : distance à la vitesse de référence + 30 s de marge.</summary>
        private double ComputeTimeLimit(float meters)
        {
            double speed = Math.Max(1, _settings.ReferenceSpeedKmh) / 3.6;
            return meters / speed + 30;
        }

        // ==================================================================
        //  Appels de joueurs
        // ==================================================================

        private void CallTaxi(Player caller, string message)
        {
            if (!_settings.AllowPlayerCalls)
            {
                Notify(caller, "Le service d'appel de taxi est désactivé.", NotificationManager.Type.Error);
                return;
            }
            if (_calls.Any(c => c.CallerId == caller.netId))
            {
                Notify(caller, "Vous avez déjà un appel en cours (/appeltaxi annuler).", NotificationManager.Type.Warning);
                return;
            }

            List<Player> drivers = AvailableDrivers();
            if (drivers.Count == 0)
            {
                Notify(caller, "Aucun chauffeur de taxi n'est en service pour le moment.", NotificationManager.Type.Warning);
                return;
            }

            TaxiCall call = new TaxiCall
            {
                Id = _nextCallId++,
                CallerId = caller.netId,
                CallerName = caller.FullName,
                Position = Position(caller),
                Message = message?.Trim(),
                CreatedAt = DateTime.Now
            };
            _calls.Add(call);

            Notify(caller, $"Appel envoyé à {drivers.Count} chauffeur(s). Restez sur place.", NotificationManager.Type.Success);
            string text = string.IsNullOrWhiteSpace(call.Message) ? "" : $" : « {call.Message} »";
            foreach (Player driver in drivers)
                Notify(driver, $"Appel client de {call.CallerName}{text}. Acceptez-le avec /taxi.", NotificationManager.Type.Info);
        }

        private void CancelCall(Player caller)
        {
            TaxiCall call = _calls.FirstOrDefault(c => c.CallerId == caller.netId);
            if (call == null)
            {
                Notify(caller, "Vous n'avez aucun appel en cours.", NotificationManager.Type.Info);
                return;
            }
            RemoveCall(call, "Le client a annulé son appel.");
            Notify(caller, "Appel annulé.", NotificationManager.Type.Info);
        }

        private void AcceptCall(Player driver, TaxiCall call)
        {
            if (!CanWork(driver)) return;
            if (_rides.ContainsKey(driver.netId))
            {
                Notify(driver, "Terminez ou annulez d'abord votre course en cours.", NotificationManager.Type.Warning);
                return;
            }
            if (!_calls.Contains(call) || call.TakenBy != 0)
            {
                Notify(driver, "Cet appel a déjà été pris ou annulé.", NotificationManager.Type.Warning);
                return;
            }
            if (!CheckVehicle(driver)) return;

            call.TakenBy = driver.netId;
            Ride ride = new Ride { DriverId = driver.netId, Stage = RideStage.ToPickup, Call = call, StageStart = DateTime.Now };
            _rides[driver.netId] = ride;
            SetRideCheckpoint(driver, ride, call.Position, OnCallerReached);

            Notify(driver, $"Appel accepté : rejoignez {call.CallerName} (point orange).", NotificationManager.Type.Success);
            Notify(GetPlayer(call.CallerId), $"Le taxi de {driver.FullName} arrive !", NotificationManager.Type.Success);
        }

        private void OnCallerReached(Player driver, Ride ride)
        {
            _rides.Remove(driver.netId);
            _calls.Remove(ride.Call);

            Notify(driver, $"Vous êtes arrivé. {ride.Call.CallerName} vous attend : convenez du prix de la course.", NotificationManager.Type.Success);
            Notify(GetPlayer(ride.Call.CallerId), "Votre taxi est arrivé !", NotificationManager.Type.Success);

            if (_settings.CallBonus > 0)
            {
                driver.AddMoney(_settings.CallBonus, "Prime appel taxi");
                Notify(driver, $"Prime de prise en charge : +{_settings.CallBonus}€", NotificationManager.Type.Success);
                _ = AddStats(driver, _settings.CallBonus, 0);
            }
            AddRideToHistory(driver.netId);
        }

        private List<TaxiCall> PendingCalls() => _calls.Where(c => c.TakenBy == 0).ToList();

        private List<Player> AvailableDrivers() =>
            Nova.server.Players.Where(p => IsTaxi(p) && (_settings.RequireService ? _onDuty.Contains(p.netId) : true)).ToList();

        private void RemoveCall(TaxiCall call, string reasonForDriver)
        {
            _calls.Remove(call);
            if (call.TakenBy != 0 && _rides.TryGetValue(call.TakenBy, out Ride ride) && ride.Call == call)
            {
                Player driver = GetPlayer(call.TakenBy);
                ClearRideCheckpoint(driver, ride);
                _rides.Remove(call.TakenBy);
                Notify(driver, reasonForDriver, NotificationManager.Type.Warning);
            }
        }

        // ==================================================================
        //  Points orange (véhicule) et état des courses
        // ==================================================================

        /// <summary>
        /// Les points de course sont des NVehicleCheckpoint : ils ne se déclenchent qu'au volant
        /// d'un véhicule (l'ancien plugin utilisait des points bleus à pied).
        /// </summary>
        private void SetRideCheckpoint(Player p, Ride ride, Vector3 position, Action<Player, Ride> onReached)
        {
            ClearRideCheckpoint(p, ride);

            NVehicleCheckpoint checkpoint = null;
            checkpoint = new NVehicleCheckpoint(p.netId, position,
                (Action<NVehicleCheckpoint, uint>)((triggered, vehicleId) => TryReach(p, ride, checkpoint, true)));

            ride.Checkpoint = checkpoint;
            ride.Target = position;
            ride.OnReached = onReached;
            ride.LastWarning = DateTime.MinValue;
            p.CreateVehicleCheckpoint(checkpoint);
        }

        /// <summary>Le chauffeur atteint le point de sa course (signal du jeu ou détection serveur).</summary>
        private void TryReach(Player p, Ride ride, NVehicleCheckpoint checkpoint, bool fromGame)
        {
            // Ignore les déclenchements d'un ancien point ou d'une course annulée
            if (p == null || !_rides.TryGetValue(p.netId, out Ride current) || current != ride || ride.Checkpoint != checkpoint) return;

            // Le point reste en place tant que le chauffeur n'est pas dans un taxi autorisé
            string problem = VehicleProblem(p);
            if (problem != null)
            {
                if ((DateTime.Now - ride.LastWarning).TotalSeconds >= 5)
                {
                    ride.LastWarning = DateTime.Now;
                    Notify(p, problem, NotificationManager.Type.Warning);
                }
                return;
            }

            ClearRideCheckpoint(p, ride);
            ride.OnReached(p, ride);
        }

        /// <summary>
        /// Détection côté serveur, appelée plusieurs fois par seconde. Le jeu n'envoie qu'un seul signal
        /// quand la voiture entre dans le point orange et le refuse si la position du personnage, en retard
        /// sur le réseau, est à plus de 10 m : en roulant vite le point ne se validait jamais.
        /// Ici on compare directement la position de la voiture au point, sans dépendre de ce signal.
        /// </summary>
        internal void Tick()
        {
            if (_rides.Count == 0) return;
            float radius = Math.Max(2, _settings.TriggerRadius);

            foreach (Ride ride in _rides.Values.ToList())
            {
                if (ride.Checkpoint == null) continue;
                Player p = GetPlayer(ride.DriverId);
                Life.VehicleSystem.Vehicle vehicle = DrivenVehicle(p);
                if (vehicle == null) continue;
                if (Vector3.Distance(vehicle.transform.position, ride.Target) <= radius)
                    TryReach(p, ride, ride.Checkpoint, false);
            }
        }

        private static void ClearRideCheckpoint(Player p, Ride ride)
        {
            if (ride?.Checkpoint == null) return;
            Nova.server.vehicleCheckpoints?.Remove(ride.Checkpoint);
            p?.DestroyVehicleCheckpoint(ride.Checkpoint);
            ride.Checkpoint = null;
        }

        private void CancelRide(Player p, bool verbose)
        {
            if (p == null || !_rides.TryGetValue(p.netId, out Ride ride))
            {
                if (verbose && p != null) Notify(p, "Vous n'avez aucune course en cours.", NotificationManager.Type.Info);
                return;
            }

            ClearRideCheckpoint(p, ride);
            _rides.Remove(p.netId);

            if (ride.Call != null && _calls.Contains(ride.Call))
            {
                ride.Call.TakenBy = 0;
                Notify(GetPlayer(ride.Call.CallerId), "Votre taxi a annulé. Votre appel est de nouveau en attente.", NotificationManager.Type.Warning);
            }
            Notify(p, "Course annulée.", NotificationManager.Type.Info);
        }

        private void ResetPlayer(uint netId)
        {
            _onDuty.Remove(netId);
            if (_rides.TryGetValue(netId, out Ride ride))
            {
                ClearRideCheckpoint(GetPlayer(netId), ride);
                _rides.Remove(netId);
            }
            foreach (TaxiCall call in _calls.Where(c => c.CallerId == netId).ToList())
                RemoveCall(call, "Le client n'est plus disponible.");
            foreach (TaxiCall call in _calls.Where(c => c.TakenBy == netId))
                call.TakenBy = 0;
        }

        /// <summary>Nettoyage chaque minute : joueurs déconnectés et appels expirés.</summary>
        private void OnMinutePassed()
        {
            HashSet<uint> online = new HashSet<uint>(Nova.server.Players.Select(p => p.netId));

            foreach (uint id in _onDuty.Concat(_rides.Keys).Where(id => !online.Contains(id)).ToList())
                ResetPlayer(id);
            foreach (uint id in _calls.Select(c => c.CallerId).Where(id => !online.Contains(id)).ToList())
                ResetPlayer(id);

            if (_settings.CallExpireMinutes > 0)
            {
                foreach (TaxiCall call in _calls.Where(c => (DateTime.Now - c.CreatedAt).TotalMinutes >= _settings.CallExpireMinutes).ToList())
                {
                    Notify(GetPlayer(call.CallerId), "Votre appel de taxi a expiré.", NotificationManager.Type.Warning);
                    RemoveCall(call, "L'appel du client a expiré.");
                }
            }
        }

        private int RidesLastHour(uint netId)
        {
            if (!_rideHistory.TryGetValue(netId, out List<DateTime> list)) return 0;
            list.RemoveAll(d => (DateTime.Now - d).TotalHours >= 1);
            return list.Count;
        }

        private void AddRideToHistory(uint netId)
        {
            if (!_rideHistory.TryGetValue(netId, out List<DateTime> list))
                _rideHistory[netId] = list = new List<DateTime>();
            list.Add(DateTime.Now);
        }

        private static string RideDescription(Ride ride)
        {
            if (ride.Call != null) return $"aller chercher {ride.Call.CallerName}";
            return ride.Stage == RideStage.ToPickup ? $"client à {ride.Pickup.Name}" : $"vers {ride.Dropoff.Name} ({ride.Fare}€)";
        }

        // ==================================================================
        //  Statistiques
        // ==================================================================

        private async Task AddStats(Player p, int earned, float distance)
        {
            try
            {
                int characterId = p.character.Id;
                TaxiDriverStats stats = (await TaxiDriverStats.Query(s => s.CharacterId == characterId)).FirstOrDefault()
                                        ?? new TaxiDriverStats { CharacterId = characterId };
                stats.Name = p.FullName;
                stats.Rides++;
                stats.Earned += earned;
                stats.Distance += (int)distance;
                await stats.Save();
            }
            catch (Exception e)
            {
                Logger.LogWarning(PluginInformations.SourceName, $"Statistiques non enregistrées : {e.Message}");
            }
        }

        private async Task MyStatsMenu(Player player)
        {
            int characterId = player.character.Id;
            TaxiDriverStats stats = (await TaxiDriverStats.Query(s => s.CharacterId == characterId)).FirstOrDefault() ?? new TaxiDriverStats();

            Panel panel = PanelHelper.Create("Taxi - Mes statistiques", UIPanel.PanelType.Tab, player, async () => await MyStatsMenu(player));
            panel.AddTabLine($"Courses effectuées : {stats.Rides}", _ => { });
            panel.AddTabLine($"Argent gagné : {stats.Earned}€", _ => { });
            panel.AddTabLine($"Distance parcourue avec client : {stats.Distance / 1000.0:0.0} km", _ => { });
            panel.AddTabLine($"Courses cette heure : {RidesLastHour(player.netId)}" + (_settings.MaxRidesPerHour > 0 ? $" / {_settings.MaxRidesPerHour}" : ""), _ => { });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task LeaderboardMenu(Player player, bool staff)
        {
            List<TaxiDriverStats> all = (await TaxiDriverStats.QueryAll()).OrderByDescending(s => s.Earned).ToList();
            string action = "";

            Panel panel = PanelHelper.Create("Taxi - Classement", UIPanel.PanelType.Tab, player, async () => await LeaderboardMenu(player, staff));
            if (all.Count == 0) panel.AddTabLine("Aucune course effectuée pour le moment", _ => { });

            int rank = 1;
            foreach (TaxiDriverStats stats in all.Take(staff ? 50 : 10))
            {
                panel.AddTabLine($"#{rank++} {stats.Name} - {stats.Rides} courses - {stats.Earned}€", async _ =>
                {
                    if (staff && action == "reset" && await stats.Delete())
                    {
                        Notify(player, $"Statistiques de {stats.Name} remises à zéro.", NotificationManager.Type.Success);
                        panel.Refresh();
                    }
                });
            }

            if (staff && all.Count > 0)
                panel.AddButton(Col("Remettre à zéro", Red), _ => { action = "reset"; panel.SelectTab(); });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Staff : configuration (AAMenu > Points bleus > Taxi, ou /taxiadmin)
        // ==================================================================

        private static bool IsStaff(Player player) => player != null && player.IsAdmin;

        public void OpenAdminMenu(Player player)
        {
            if (!IsStaff(player)) return;

            Panel panel = PanelHelper.Create("Taxi - Configuration (staff)", UIPanel.PanelType.Tab, player, () => OpenAdminMenu(player));
            panel.TextLines.Add(Col($"TaxiJob v{PluginInformations.Version} - By {Author}", Gold));
            panel.AddTabLine("Paramètres du job (paie, entreprise, règles...)", _ => SettingsMenu(player));
            panel.AddTabLine($"Points de course ({_points.Count(p => p.Enabled)} actifs / {_points.Count})", _ => PointsMenu(player));
            panel.AddTabLine($"Véhicules autorisés ({(_settings.AllowedModelIds.Count == 0 ? "tous" : _settings.AllowedModelIds.Count.ToString())})", _ => VehiclesMenu(player));
            panel.AddTabLine("Centrales taxi (points bleus) : placer ici", async _ => await PlaceStationMenu(player));
            panel.AddTabLine("Centrales taxi : liste des points placés", async _ => await StationPointsMenu(player));
            panel.AddTabLine("Centrales taxi : modèles", async _ => await StationModelsMenu(player, true));
            panel.AddTabLine($"Courses en cours ({_rides.Count})", _ => ActiveRidesMenu(player));
            panel.AddTabLine($"Appels clients en attente ({PendingCalls().Count})", _ => AdminCallsMenu(player));
            panel.AddTabLine("Classement / remise à zéro des statistiques", async _ => await LeaderboardMenu(player, true));

            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Paramètres ----------------

        private static readonly List<SettingField> SettingFields = new List<SettingField>
        {
            SettingField.Int("ID de l'entreprise taxi (0 = ouvert à tous, comme un farm)", s => s.BizId, (s, v) => s.BizId = v, 0, 100000),
            SettingField.Bool("Prise de service obligatoire", s => s.RequireService, (s, v) => s.RequireService = v),
            SettingField.Bool("Être au volant pour lancer une course", s => s.RequireVehicle, (s, v) => s.RequireVehicle = v),
            SettingField.Bool("Véhicule de l'entreprise taxi obligatoire", s => s.RequireBizVehicle, (s, v) => s.RequireBizVehicle = v),
            SettingField.Int("Rayon de validation d'un point (m)", s => s.TriggerRadius, (s, v) => s.TriggerRadius = v, 2, 50),
            SettingField.Int("Prise en charge (€)", s => s.BasePay, (s, v) => s.BasePay = v, 0, 1000000),
            SettingField.Int("Prix au kilomètre (€)", s => s.PayPerKm, (s, v) => s.PayPerKm = v, 0, 1000000),
            SettingField.Int("Paie minimum par course (€, 0 = aucun)", s => s.MinPay, (s, v) => s.MinPay = v, 0, 1000000),
            SettingField.Int("Paie maximum par course (€, 0 = aucun)", s => s.MaxPay, (s, v) => s.MaxPay = v, 0, 1000000),
            SettingField.Int("Pourboire si course rapide (%)", s => s.TipPercent, (s, v) => s.TipPercent = v, 0, 1000),
            SettingField.Int("Vitesse de référence pour le pourboire (km/h)", s => s.ReferenceSpeedKmh, (s, v) => s.ReferenceSpeedKmh = v, 1, 500),
            SettingField.Int("Distance minimum d'une course (m)", s => s.MinRideDistance, (s, v) => s.MinRideDistance = v, 0, 100000),
            SettingField.Bool("Enchaîner les courses automatiquement", s => s.AutoChain, (s, v) => s.AutoChain = v),
            SettingField.Int("Courses max par heure (0 = illimité)", s => s.MaxRidesPerHour, (s, v) => s.MaxRidesPerHour = v, 0, 1000),
            SettingField.Bool("Appels de joueurs (/appeltaxi)", s => s.AllowPlayerCalls, (s, v) => s.AllowPlayerCalls = v),
            SettingField.Int("Expiration d'un appel (min, 0 = jamais)", s => s.CallExpireMinutes, (s, v) => s.CallExpireMinutes = v, 0, 1440),
            SettingField.Int("Prime au chauffeur par appel joueur (€)", s => s.CallBonus, (s, v) => s.CallBonus = v, 0, 1000000),
        };

        private void SettingsMenu(Player player)
        {
            if (!IsStaff(player)) return;

            Panel panel = PanelHelper.Create("Taxi - Paramètres", UIPanel.PanelType.Tab, player, () => SettingsMenu(player));
            foreach (SettingField field in SettingFields)
            {
                panel.AddTabLine($"{field.Label} : {Col(field.Display(_settings), Gold)}", async _ =>
                {
                    if (field.IsBool)
                    {
                        field.Set(_settings, field.Get(_settings) == 1 ? 0 : 1);
                        if (await SaveSettings(player))
                            Notify(player, $"{field.Label} : {field.Display(_settings)}", NotificationManager.Type.Success);
                        panel.Refresh();
                    }
                    else
                    {
                        EditSettingMenu(player, field);
                    }
                });
            }

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditSettingMenu(Player player, SettingField field)
        {
            Panel panel = PanelHelper.Create("Taxi - Modifier un paramètre", UIPanel.PanelType.Input, player, () => EditSettingMenu(player, field));
            panel.TextLines.Add(field.Label);
            panel.TextLines.Add($"Valeur actuelle : {field.Display(_settings)} (entre {field.Min} et {field.Max})");
            panel.inputPlaceholder = field.Get(_settings).ToString();

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int value) || value < field.Min || value > field.Max)
                {
                    Notify(player, $"Valeur invalide : entrez un nombre entre {field.Min} et {field.Max}.", NotificationManager.Type.Error);
                    return false;
                }
                field.Set(_settings, value);
                if (!await SaveSettings(player)) return false;
                Notify(player, $"{field.Label} : {field.Display(_settings)}", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Points de course ----------------

        private void PointsMenu(Player player)
        {
            if (!IsStaff(player)) return;

            Vector3 here = Position(player);
            Panel panel = PanelHelper.Create("Taxi - Points de course", UIPanel.PanelType.Tab, player, () => PointsMenu(player));
            panel.TextLines.Add("Les clients PNJ sont pris en charge et déposés sur ces points (en voiture).");

            if (_points.Count == 0) panel.AddTabLine("Aucun point : placez-vous en voiture et « Ajouter ici »", _ => { });
            foreach (TaxiPoint point in _points.OrderBy(p => p.Id))
            {
                string state = point.Enabled ? point.TypeLabel : Col("désactivé", Red);
                panel.AddTabLine($"[{point.Id}] {point.Name} - {state} - {(int)Vector3.Distance(here, point.Position)} m", _ => PointMenu(player, point));
            }

            if (_points.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.NextButton("Ajouter ici", () => CreatePointMenu(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void CreatePointMenu(Player player)
        {
            Panel panel = PanelHelper.Create("Taxi - Nouveau point de course", UIPanel.PanelType.Input, player, () => CreatePointMenu(player));
            panel.TextLines.Add("Nom du lieu (ex. Gare, Hôpital, Mairie...). Le point est créé à votre position.");
            panel.TextLines.Add("Placez-vous sur la route : le chauffeur doit pouvoir y passer en voiture.");
            panel.inputPlaceholder = $"Arrêt {_points.Count + 1}";

            panel.PreviousButtonWithAction("Créer ici", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) name = $"Arrêt {_points.Count + 1}";

                Vector3 pos = Position(player);
                TaxiPoint point = new TaxiPoint { Name = name, X = pos.x, Y = pos.y, Z = pos.z, IsPickup = true, IsDropoff = true, Enabled = true };
                if (!await point.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement du point.", NotificationManager.Type.Error);
                    return false;
                }
                await ReloadPoints();
                Notify(player, $"Point « {name} » créé à votre position.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void PointMenu(Player player, TaxiPoint point)
        {
            Panel panel = PanelHelper.Create($"Taxi - Point « {point.Name} »", UIPanel.PanelType.Tab, player, () => PointMenu(player, point));
            panel.TextLines.Add($"Type : {point.TypeLabel} - {(point.Enabled ? "actif" : "désactivé")}");

            panel.AddTabLine("Renommer", _ => RenamePointMenu(player, point));
            panel.AddTabLine($"Type : {Col(point.TypeLabel, Gold)} (changer)", async _ =>
            {
                // Cycle : prise en charge + dépose -> prise en charge seule -> dépose seule
                if (point.IsPickup && point.IsDropoff) { point.IsDropoff = false; }
                else if (point.IsPickup) { point.IsPickup = false; point.IsDropoff = true; }
                else { point.IsPickup = true; point.IsDropoff = true; }
                await SavePoint(player, point, $"Type : {point.TypeLabel}");
                panel.Refresh();
            });
            panel.AddTabLine(point.Enabled ? Col("Désactiver", Red) : Col("Activer", Green), async _ =>
            {
                point.Enabled = !point.Enabled;
                await SavePoint(player, point, point.Enabled ? "Point activé" : "Point désactivé");
                panel.Refresh();
            });
            panel.AddTabLine("Déplacer à ma position", async _ =>
            {
                Vector3 pos = Position(player);
                point.X = pos.x; point.Y = pos.y; point.Z = pos.z;
                await SavePoint(player, point, "Point déplacé à votre position");
            });
            panel.AddTabLine("Se téléporter au point", _ => player.setup.TargetSetPosition(point.Position));
            panel.AddTabLine(Col("Supprimer", Red), async _ =>
            {
                if (await point.Delete())
                {
                    await ReloadPoints();
                    Notify(player, $"Point « {point.Name} » supprimé.", NotificationManager.Type.Success);
                    PointsMenu(player);
                }
                else Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
            });

            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePointMenu(Player player, TaxiPoint point)
        {
            Panel panel = PanelHelper.Create("Taxi - Renommer le point", UIPanel.PanelType.Input, player, () => RenamePointMenu(player, point));
            panel.TextLines.Add($"Nouveau nom pour « {point.Name} »");
            panel.inputPlaceholder = point.Name;
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                point.Name = name;
                return await SavePoint(player, point, $"Point renommé en « {name} »");
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> SavePoint(Player player, TaxiPoint point, string successMessage)
        {
            bool ok = await point.Save();
            await ReloadPoints();
            Notify(player, ok ? successMessage : "Erreur lors de l'enregistrement du point.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error);
            return ok;
        }

        // ---------------- Véhicules autorisés ----------------

        private void VehiclesMenu(Player player)
        {
            if (!IsStaff(player)) return;

            List<int> allowed = _settings.AllowedModelIds;
            Panel panel = PanelHelper.Create("Taxi - Véhicules autorisés", UIPanel.PanelType.Tab, player, () => VehiclesMenu(player));
            panel.TextLines.Add("Liste vide : tous les véhicules sont acceptés.");

            if (allowed.Count == 0) panel.AddTabLine("Tous les véhicules sont autorisés", _ => { });
            foreach (int modelId in allowed)
            {
                panel.AddTabLine($"[{modelId}] {ModelName(modelId)} - {Col("retirer", Red)}", async _ =>
                {
                    _settings.AllowedModelIds = _settings.AllowedModelIds.Where(id => id != modelId).ToList();
                    if (await SaveSettings(player))
                        Notify(player, $"{ModelName(modelId)} retiré des taxis.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }

            panel.AddButton("Ajouter mon véhicule", async _ =>
            {
                int modelId = DrivenModelId(player);
                if (modelId == -1)
                {
                    Notify(player, "Montez au volant du véhicule à autoriser.", NotificationManager.Type.Warning);
                    return;
                }
                if (modelId < 0)
                {
                    Notify(player, "Modèle inconnu (véhicule non enregistré). Utilisez plutôt « Véhicule de l'entreprise taxi obligatoire ».", NotificationManager.Type.Warning);
                    return;
                }
                if (!_settings.AllowedModelIds.Contains(modelId))
                {
                    _settings.AllowedModelIds = _settings.AllowedModelIds.Append(modelId).ToList();
                    if (await SaveSettings(player))
                        Notify(player, $"{ModelName(modelId)} ajouté aux taxis.", NotificationManager.Type.Success);
                }
                panel.Refresh();
            });
            if (allowed.Count > 0) panel.AddButton(Col("Retirer", Red), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string ModelName(int modelId)
        {
            try { return VehicleUtils.GetModelNameByModelId(modelId); }
            catch { return $"Modèle {modelId}"; }
        }

        // ---------------- Suivi en direct ----------------

        private void ActiveRidesMenu(Player player)
        {
            Panel panel = PanelHelper.Create("Taxi - Courses en cours", UIPanel.PanelType.Tab, player, () => ActiveRidesMenu(player));
            if (_rides.Count == 0) panel.AddTabLine("Aucune course en cours", _ => { });

            foreach (Ride ride in _rides.Values.ToList())
            {
                Player driver = GetPlayer(ride.DriverId);
                panel.AddTabLine($"{driver?.FullName ?? "?"} : {RideDescription(ride)}", _ =>
                {
                    if (driver != null)
                    {
                        CancelRide(driver, false);
                        Notify(driver, "Votre course a été annulée par le staff.", NotificationManager.Type.Warning);
                    }
                    else _rides.Remove(ride.DriverId);
                    Notify(player, "Course annulée.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }

            if (_rides.Count > 0) panel.AddButton(Col("Annuler la course", Red), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminCallsMenu(Player player)
        {
            Panel panel = PanelHelper.Create("Taxi - Appels clients", UIPanel.PanelType.Tab, player, () => AdminCallsMenu(player));
            if (_calls.Count == 0) panel.AddTabLine("Aucun appel", _ => { });

            foreach (TaxiCall call in _calls.ToList())
            {
                string taken = call.TakenBy != 0 ? $"pris par {GetPlayer(call.TakenBy)?.FullName ?? "?"}" : "en attente";
                panel.AddTabLine($"#{call.Id} {call.CallerName} - {taken}", _ =>
                {
                    Notify(GetPlayer(call.CallerId), "Votre appel de taxi a été annulé par le staff.", NotificationManager.Type.Warning);
                    RemoveCall(call, "L'appel a été annulé par le staff.");
                    panel.Refresh();
                });
            }

            if (_calls.Count > 0) panel.AddButton(Col("Supprimer l'appel", Red), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Centrales taxi (points bleus) ----------------

        public async Task PlaceStationMenu(Player player)
        {
            if (!IsStaff(player)) return;
            List<TaxiStation> models = await TaxiStation.QueryAll();

            Panel panel = PanelHelper.Create("Taxi - Placer une centrale", UIPanel.PanelType.Tab, player, async () => await PlaceStationMenu(player));
            panel.TextLines.Add("Une centrale est un point bleu où les chauffeurs ouvrent le menu taxi (service, courses...).");

            if (models.Count == 0) panel.AddTabLine("Aucun modèle : créez-en un (« Nouveau modèle »)", _ => { });
            foreach (TaxiStation model in models)
            {
                panel.AddTabLine($"[{model.Id}] {model.PatternName}", async _ =>
                {
                    model.TypeName = nameof(TaxiStation);
                    model.Context = this;
                    if (await PointHelper.CreateNPoint(player, model))
                        Notify(player, $"Centrale « {model.PatternName} » placée à votre position.", NotificationManager.Type.Success);
                    else
                        Notify(player, "Erreur lors de la création du point.", NotificationManager.Type.Error);
                });
            }

            if (models.Count > 0) panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => CreateStationModel(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void CreateStationModel(Player player)
        {
            Panel panel = PanelHelper.Create("Taxi - Nouveau modèle de centrale", UIPanel.PanelType.Input, player, () => CreateStationModel(player));
            panel.TextLines.Add("Nom de la centrale (affiché aux joueurs)");
            panel.inputPlaceholder = "Centrale taxi";

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) name = "Centrale taxi";
                if (!await new TaxiStation(false) { PatternName = name }.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }
                Notify(player, $"Modèle « {name} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task StationModelsMenu(Player player, bool forEdit)
        {
            List<TaxiStation> models = await TaxiStation.QueryAll();
            string action = "";

            Panel panel = PanelHelper.Create("Taxi - Modèles de centrale", UIPanel.PanelType.Tab, player, async () => await StationModelsMenu(player, forEdit));
            if (models.Count == 0) panel.AddTabLine("Aucun modèle", _ => { });

            foreach (TaxiStation model in models)
            {
                panel.AddTabLine($"[{model.Id}] {model.PatternName}", async _ =>
                {
                    model.TypeName = nameof(TaxiStation);
                    model.Context = this;
                    if (action == "rename")
                    {
                        RenameStationModel(player, model);
                    }
                    else if (action == "delete")
                    {
                        await PointHelper.DeleteNPointsByPattern(player, model);
                        if (await model.Delete())
                            Notify(player, $"Modèle « {model.PatternName} » et ses centrales supprimés.", NotificationManager.Type.Success);
                        else
                            Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                        panel.Refresh();
                    }
                });
            }

            if (models.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Col("Supprimer", Red), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.NextButton("Nouveau modèle", () => CreateStationModel(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenameStationModel(Player player, TaxiStation model)
        {
            Panel panel = PanelHelper.Create("Taxi - Renommer la centrale", UIPanel.PanelType.Input, player, () => RenameStationModel(player, model));
            panel.TextLines.Add($"Nouveau nom pour « {model.PatternName} »");
            panel.inputPlaceholder = model.PatternName ?? "Centrale taxi";
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                model.PatternName = name;
                return await model.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task StationPointsMenu(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(TaxiStation));
            Dictionary<int, string> names = (await TaxiStation.QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = PanelHelper.Create("Taxi - Centrales placées", UIPanel.PanelType.Tab, player, async () => await StationPointsMenu(player));
            if (points.Count == 0) panel.AddTabLine("Aucune centrale placée", _ => { });

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
                                Notify(player, "Centrale déplacée à votre position.", NotificationManager.Type.Success);
                            break;
                        case "delete":
                            await PointHelper.DeleteNPoint(point);
                            Notify(player, $"Centrale #{point.Id} supprimée.", NotificationManager.Type.Success);
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Col("Supprimer", Red), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Utilitaires
        // ==================================================================

        private const string Green = "#2ecc71";
        private const string Red = "#e74c3c";
        private const string Gold = "#f1c40f";

        private static string Col(string text, string hex) => $"<color={hex}>{text}</color>";

        private static string YesNo(bool value) => value ? Col("Oui", Green) : Col("Non", Red);

        private static string FormatDuration(double seconds)
        {
            int s = (int)Math.Round(seconds);
            return s >= 60 ? $"{s / 60} min {s % 60:00} s" : $"{s} s";
        }

        private static void Notify(Player player, string message, NotificationManager.Type type)
        {
            player?.Notify(Title, message, type, 6f);
        }

        private static Player GetPlayer(uint netId) => Nova.server.Players.FirstOrDefault(p => p.netId == netId);

        /// <summary>Position du joueur, ou de sa voiture s'il conduit (plus fiable que le personnage en roulant).</summary>
        private static Vector3 Position(Player player)
        {
            Life.VehicleSystem.Vehicle vehicle = DrivenVehicle(player);
            return vehicle != null ? vehicle.transform.position : player.setup.transform.position;
        }

        private T Pick<T>(List<T> list) => list[_rng.Next(list.Count)];
    }
}

namespace TaxiJob
{
    // ======================================================================
    //  Tables de la base ModKit (Plugins/ModKit/data.sqlite)
    // ======================================================================

    /// <summary>Paramètres du job (une seule ligne), modifiables en jeu par le staff.</summary>
    public class TaxiSettings : ModEntity<TaxiSettings>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; } = 0;
        public bool RequireService { get; set; } = true;
        public int BasePay { get; set; } = 50;
        public int PayPerKm { get; set; } = 150;
        public int MinPay { get; set; } = 80;
        public int MaxPay { get; set; } = 600;
        public int TipPercent { get; set; } = 20;
        public int ReferenceSpeedKmh { get; set; } = 50;
        public int MinRideDistance { get; set; } = 300;
        public bool AutoChain { get; set; } = false;
        public int MaxRidesPerHour { get; set; } = 0;
        public bool AllowPlayerCalls { get; set; } = true;
        public int CallExpireMinutes { get; set; } = 10;
        public int CallBonus { get; set; } = 0;
        public bool RequireVehicle { get; set; } = true;
        public bool RequireBizVehicle { get; set; } = false;
        public int TriggerRadius { get; set; } = 8;

        /// <summary>Modèles de véhicule autorisés, séparés par des virgules (vide = tous).</summary>
        public string AllowedModels { get; set; } = "";

        [Ignore]
        public List<int> AllowedModelIds
        {
            get => (AllowedModels ?? "").Split(',').Select(x => int.TryParse(x.Trim(), out int id) ? id : -1).Where(id => id >= 0).Distinct().ToList();
            set => AllowedModels = string.Join(",", value);
        }
    }

    /// <summary>Point de course placé par le staff (prise en charge et/ou dépose d'un client PNJ).</summary>
    public class TaxiPoint : ModEntity<TaxiPoint>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool IsPickup { get; set; } = true;
        public bool IsDropoff { get; set; } = true;
        public bool Enabled { get; set; } = true;

        [Ignore]
        public Vector3 Position => new Vector3(X, Y, Z);

        [Ignore]
        public string TypeLabel => IsPickup && IsDropoff ? "prise en charge + dépose" : IsPickup ? "prise en charge" : "dépose";
    }

    /// <summary>Statistiques d'un chauffeur (par personnage).</summary>
    public class TaxiDriverStats : ModEntity<TaxiDriverStats>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string Name { get; set; }
        public int Rides { get; set; }
        public int Earned { get; set; }
        public int Distance { get; set; }
    }

    /// <summary>
    /// Centrale taxi : point bleu (PatternData) où les chauffeurs ouvrent leur menu.
    /// C'est aussi l'entrée staff dans AAMenu > Administration > Points bleus > Taxi.
    /// </summary>
    public class TaxiStation : ModEntity<TaxiStation>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(TaxiStation);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public TaxiStation() { }

        public TaxiStation(bool isCreated)
        {
            TypeName = nameof(TaxiStation);
        }

        public void OnPlayerTrigger(Player player) => TaxiJobPlugin.Instance?.OpenDriverMenu(player);

        public async Task SetProperties(int id)
        {
            TaxiStation result = await Query(id);
            Id = id;
            TypeName = nameof(TaxiStation);
            PatternName = result?.PatternName;
        }

        public void CreateOrGenerate(Player player) => TaxiJobPlugin.Instance?.OpenAdminMenu(player);

        public void SetPatternData(Player player) => TaxiJobPlugin.Instance?.CreateStationModel(player);

        public Task GetPatternData(Player player, bool forEdit) =>
            TaxiJobPlugin.Instance?.StationModelsMenu(player, forEdit) ?? Task.CompletedTask;

        public Task GetNPoints(Player player) =>
            TaxiJobPlugin.Instance?.StationPointsMenu(player) ?? Task.CompletedTask;
    }

    // ======================================================================
    //  État en mémoire
    // ======================================================================

    /// <summary>Composant Unity qui appelle TaxiJobPlugin.Tick 4 fois par seconde (sur le thread principal).</summary>
    internal sealed class TaxiTicker : MonoBehaviour
    {
        private const float Interval = 0.25f;
        private TaxiJobPlugin _plugin;
        private float _next;

        public static void Start(TaxiJobPlugin plugin)
        {
            GameObject go = new GameObject("TaxiJobTicker");
            DontDestroyOnLoad(go);
            go.AddComponent<TaxiTicker>()._plugin = plugin;
        }

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Interval;
            try { _plugin?.Tick(); }
            catch (Exception e) { Debug.LogError($"[TaxiJob] {e}"); }
        }
    }

    internal enum RideStage { ToPickup, ToDropoff }

    internal sealed class Ride
    {
        public uint DriverId;
        public RideStage Stage;
        public TaxiPoint Pickup;
        public TaxiPoint Dropoff;
        public TaxiCall Call;
        public NVehicleCheckpoint Checkpoint;
        public Vector3 Target;
        public Action<Player, Ride> OnReached;
        public DateTime LastWarning;
        public DateTime StageStart;
        public float Distance;
        public int Fare;
        public double TimeLimit;
    }

    internal sealed class TaxiCall
    {
        public int Id;
        public uint CallerId;
        public string CallerName;
        public Vector3 Position;
        public string Message;
        public DateTime CreatedAt;
        public uint TakenBy;
    }

    /// <summary>Description d'un paramètre éditable dans le menu staff.</summary>
    internal sealed class SettingField
    {
        public string Label;
        public bool IsBool;
        public int Min;
        public int Max;
        public Func<TaxiSettings, int> Get;
        public Action<TaxiSettings, int> Set;

        public string Display(TaxiSettings s) => IsBool ? (Get(s) == 1 ? "Oui" : "Non") : Get(s).ToString();

        public static SettingField Int(string label, Func<TaxiSettings, int> get, Action<TaxiSettings, int> set, int min, int max) =>
            new SettingField { Label = label, Get = get, Set = set, Min = min, Max = max };

        public static SettingField Bool(string label, Func<TaxiSettings, bool> get, Action<TaxiSettings, bool> set) =>
            new SettingField { Label = label, IsBool = true, Min = 0, Max = 1, Get = s => get(s) ? 1 : 0, Set = (s, v) => set(s, v == 1) };
    }

    // Format de l'ancien plugin jobtaxibymatheo (config.json), pour l'import automatique
    internal sealed class OldConfig
    {
        public int BizId { get; set; } = 1;
        public int MinPay { get; set; } = 137;
        public int MaxPay { get; set; } = 320;
        public OldPos StartPoint { get; set; }
        public List<OldPos> Points { get; set; }
    }

    internal sealed class OldPos
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
}
