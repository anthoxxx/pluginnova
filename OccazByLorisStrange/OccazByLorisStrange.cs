using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Life;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Life.VehicleSystem;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;
using Logger = ModKit.Internal.Logger;

namespace OccazByLorisStrange
{
    /// <summary>
    /// Occaz By Loris Strange : vente de véhicules d'occasion entre joueurs (ce n'est pas un métier).
    /// Le vendeur passe dans le point bleu vendeur, choisit sa voiture et remplit l'annonce :
    /// la voiture est déplacée sur une place de parking et verrouillée, et un point bleu apparaît
    /// devant elle. Les acheteurs marchent sur ce point pour voir l'annonce, acheter ou faire une offre.
    /// Tout se configure en jeu (AAMenu → Administration → Plugins), sans redémarrer le serveur.
    /// </summary>
    public class OccazPlugin : ModKit.ModKit
    {
        public const string Title = "Occaz by Loris Strange";
        public const string Credit = "Created by Loris Strange";

        public static OccazPlugin Instance { get; private set; }

        public OccazConfig Config { get; private set; } = new OccazConfig();

        private string ConfigPath => Path.Combine(PluginPath, AssemblyHelper.GetName(), "config.json");

        private DateTime _configStamp;
        private int _minutes;

        public OccazPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "2.0.0", "Loris Strange");
            Instance = this;
        }

        public override async void OnPluginInit()
        {
            base.OnPluginInit();

            LoadConfig();

            Orm.RegisterTable<OccazPattern>();
            Orm.RegisterTable<OccazParking>();
            Orm.RegisterTable<OccazListing>();
            Orm.RegisterTable<OccazOffer>();
            Orm.RegisterTable<OccazPayout>();

            OccazPattern pattern = new OccazPattern(false);
            PointHelper.AddPattern(nameof(OccazPattern), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Occaz (point vendeur)", pattern, this);
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Occaz : véhicule en vente à proximité",
                    ui => OccazMenus.NearbyListing(PanelHelper.ReturnPlayerFromPanel(ui)));
                // Niveau 1 ici : le niveau réel (AdminLevel) est vérifié à l'ouverture, il se change en jeu
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "Occaz By Loris Strange",
                    ui => OccazMenus.AdminMain(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogError(PluginInformations.SourceName, "AAMenu introuvable : ce plugin a besoin d'AAMenu.");
            }

            Nova.server.OnMinutePassedEvent += OnMinutePassed;

            await OccazService.LoadCache();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", $"initialisé - {Credit}");
        }

        /// <summary>Lit config.json (et le complète avec les nouvelles options).</summary>
        public void LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                if (File.Exists(ConfigPath))
                    Config = JsonConvert.DeserializeObject<OccazConfig>(File.ReadAllText(ConfigPath)) ?? new OccazConfig();
                Config.Normalize();
                SaveConfig();
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, $"Config illisible ({ConfigPath}) : {ex.Message}");
                Config ??= new OccazConfig();
            }
        }

        /// <summary>Écrit config.json : appelé à chaque modification faite en jeu.</summary>
        public void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
                _configStamp = File.GetLastWriteTimeUtc(ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, $"Impossible d'écrire {ConfigPath} : {ex.Message}");
            }
        }

        /// <summary>Recharge config.json s'il a été modifié à la main pendant que le serveur tourne.</summary>
        private void WatchConfigFile()
        {
            try
            {
                if (!File.Exists(ConfigPath) || File.GetLastWriteTimeUtc(ConfigPath) == _configStamp) return;
                LoadConfig();
                OccazInfoPoints.RefreshAll();
                Logger.LogSuccess(PluginInformations.SourceName, "config.json modifié : configuration rechargée à chaud.");
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, $"WatchConfigFile : {ex.Message}");
            }
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
            _ = OccazInfoPoints.ShowFor(player);
            _ = OccazService.PayPendingPayouts(player);
        }

        public override void OnPlayerEnterVehicle(Vehicle vehicle, int seatId, Player player)
        {
            base.OnPlayerEnterVehicle(vehicle, seatId, player);
            if (vehicle == null || !OccazService.IsListed(vehicle.VehicleDbId)) return;

            // Véhicule en vente : personne ne monte dedans, pas même le vendeur
            vehicle.EjectAll();
            vehicle.NetworkisLocked = true;
            player.Notify(Title, "Ce véhicule est en vente. Marchez sur le point bleu devant lui pour voir l'annonce.",
                NotificationManager.Type.Warning, 6f);
        }

        private void OnMinutePassed()
        {
            _minutes++;
            WatchConfigFile();
            OccazService.RelockAll();
            if (_minutes % 5 == 0)
                _ = OccazService.CheckListings();
        }
    }

    // ----------------------------------------------------------------------
    //  Configuration (modifiable en jeu)
    // ----------------------------------------------------------------------

    /// <summary>Décrit une option pour le menu de configuration en jeu.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class SettingAttribute : Attribute
    {
        public string Category { get; }
        public string Label { get; }
        public string Help { get; }
        public double Min { get; set; } = 0;
        public double Max { get; set; } = 100000000;

        public SettingAttribute(string category, string label, string help)
        {
            Category = category;
            Label = label;
            Help = help;
        }
    }

    public static class SettingCategory
    {
        public const string Sales = "Ventes et prix";
        public const string Payment = "Paiement et offres";
        public const string Parking = "Parking et points bleus";
        public const string Discord = "Discord";
        public const string Staff = "Staff";

        public static readonly string[] All = { Sales, Payment, Parking, Discord, Staff };
    }

    /// <summary>Fichier Plugins/OccazByLorisStrange/config.json. Tout se règle aussi en jeu.</summary>
    public class OccazConfig
    {
        [Setting(SettingCategory.Sales, "Commission (%)", "Retenue sur le prix de vente.", Max = 100)]
        public double CommissionPercent { get; set; } = 5;

        [Setting(SettingCategory.Sales, "Frais d'annonce", "Payés en espèces par le vendeur.")]
        public double ListingFee { get; set; } = 100;

        [Setting(SettingCategory.Sales, "Prix minimum", "Prix de vente minimum.")]
        public double MinPrice { get; set; } = 500;

        [Setting(SettingCategory.Sales, "Prix maximum", "Prix de vente maximum.", Min = 1)]
        public double MaxPrice { get; set; } = 5000000;

        [Setting(SettingCategory.Sales, "Annonces max par joueur", "Annonces en même temps.", Min = 1, Max = 50)]
        public int MaxListingsPerPlayer { get; set; } = 2;

        [Setting(SettingCategory.Sales, "Durée d'une annonce (h)", "Heures réelles avant expiration.", Min = 1, Max = 2160)]
        public int ListingDurationHours { get; set; } = 72;

        [Setting(SettingCategory.Sales, "Prévenir les joueurs", "Notification à tous à chaque annonce.")]
        public bool AnnounceNewListings { get; set; } = true;

        [Setting(SettingCategory.Sales, "Modèles interdits", "Modèles qu'on ne peut pas vendre.")]
        public List<int> BlacklistedModelIds { get; set; } = new List<int>();

        [Setting(SettingCategory.Payment, "Paiement par carte", "Autoriser la banque en plus des espèces.")]
        public bool AllowBankPayment { get; set; } = true;

        [Setting(SettingCategory.Payment, "Offres / négociation", "Les acheteurs peuvent proposer un prix.")]
        public bool AllowOffers { get; set; } = true;

        [Setting(SettingCategory.Parking, "Déplacer sur le parking", "OUI : téléportée. NON : le vendeur se gare.")]
        public bool TeleportToParking { get; set; } = true;

        [Setting(SettingCategory.Parking, "Rayon d'une place (m)", "Si le vendeur se gare lui-même.", Min = 1, Max = 20)]
        public float ParkingSpotRadius { get; set; } = 4f;

        [Setting(SettingCategory.Parking, "Distance vendeur (m)", "Voiture à moins de X m du point vendeur.", Min = 5, Max = 300)]
        public float SellerVehicleRange { get; set; } = 30f;

        [Setting(SettingCategory.Parking, "Point bleu devant la voiture", "Affiche l'annonce quand on marche dessus.")]
        public bool InfoPointEnabled { get; set; } = true;

        [Setting(SettingCategory.Parking, "Distance du point (m)", "Distance devant la voiture (placement auto).", Min = 1, Max = 15)]
        public float InfoPointDistance { get; set; } = 3.5f;

        [Setting(SettingCategory.Parking, "Ligne AAMenu Interactions", "Voir l'annonce via AAMenu > Interactions.")]
        public bool InteractionMenuEnabled { get; set; } = true;

        [Setting(SettingCategory.Parking, "Distance Interactions (m)", "Portée de la ligne Interactions.", Min = 2, Max = 50)]
        public float ProximityRange { get; set; } = 6f;

        [Setting(SettingCategory.Discord, "URL du webhook", "Vide = désactivé.")]
        public string WebhookUrl { get; set; } = "";

        [Setting(SettingCategory.Discord, "Signature Discord", "Texte en bas des messages.")]
        public string WebhookFooter { get; set; } = "Occaz - Created by Loris Strange";

        [Setting(SettingCategory.Staff, "Niveau admin minimum", "Pour ouvrir le menu staff Occaz.", Min = 1, Max = 10)]
        public int AdminLevel { get; set; } = 1;

        /// <summary>Corrige les valeurs incohérentes (fichier modifié à la main).</summary>
        public void Normalize()
        {
            BlacklistedModelIds ??= new List<int>();
            WebhookUrl ??= "";
            WebhookFooter ??= "";
            foreach (PropertyInfo property in Settings())
            {
                SettingAttribute s = property.GetCustomAttribute<SettingAttribute>();
                object value = property.GetValue(this);
                if (value is double d) property.SetValue(this, Math.Max(s.Min, Math.Min(s.Max, d)));
                else if (value is int i) property.SetValue(this, (int)Math.Max(s.Min, Math.Min(s.Max, i)));
                else if (value is float f) property.SetValue(this, (float)Math.Max(s.Min, Math.Min(s.Max, f)));
            }
            if (MaxPrice < MinPrice) MaxPrice = MinPrice;
        }

        public static IEnumerable<PropertyInfo> Settings() =>
            typeof(OccazConfig).GetProperties().Where(p => p.GetCustomAttribute<SettingAttribute>() != null);
    }

    // ----------------------------------------------------------------------
    //  Données (base SQLite de ModKit)
    // ----------------------------------------------------------------------

    public static class ListingStatus
    {
        public const string Active = "active";
        public const string Sold = "sold";
        public const string Cancelled = "cancelled";
        public const string Expired = "expired";
    }

    /// <summary>Place de parking où sont exposées les voitures en vente, avec son point bleu d'annonce.</summary>
    public class OccazParking : ModEntity<OccazParking>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float RotY { get; set; }
        /// <summary>Position du point bleu d'annonce (0,0,0 = automatique, devant la voiture).</summary>
        public float PointX { get; set; }
        public float PointY { get; set; }
        public float PointZ { get; set; }

        [Ignore] public Vector3 Position => new Vector3(X, Y, Z);

        [Ignore] public bool HasCustomPoint => PointX != 0 || PointY != 0 || PointZ != 0;

        [Ignore]
        public Vector3 InfoPosition => HasCustomPoint
            ? new Vector3(PointX, PointY, PointZ)
            : Position + Quaternion.Euler(0f, RotY, 0f) * Vector3.forward * OccazPlugin.Instance.Config.InfoPointDistance;
    }

    /// <summary>Annonce de vente. Les annonces terminées sont gardées pour l'historique.</summary>
    public class OccazListing : ModEntity<OccazListing>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int VehicleDbId { get; set; }
        public int ModelId { get; set; }
        public string Plate { get; set; }
        public string Model { get; set; }
        public double Price { get; set; }
        public int Mileage { get; set; } = -1;
        public string Condition { get; set; }
        public string Description { get; set; }
        public int SellerCharacterId { get; set; }
        public string SellerName { get; set; }
        public int ParkingId { get; set; }
        public string Status { get; set; } = ListingStatus.Active;
        public long CreatedAt { get; set; }
        public long ExpiresAt { get; set; }
        public int BuyerCharacterId { get; set; }
        public string BuyerName { get; set; }
        public double SoldPrice { get; set; }
        public double Commission { get; set; }
        public long ClosedAt { get; set; }

        [Ignore] public string RealModelName => OccazService.ModelName(ModelId);
    }

    public static class OfferStatus
    {
        public const string Pending = "pending";
        public const string Accepted = "accepted";
        public const string Refused = "refused";
        public const string Closed = "closed";
    }

    /// <summary>Offre (négociation) d'un acheteur sur une annonce.</summary>
    public class OccazOffer : ModEntity<OccazOffer>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int ListingId { get; set; }
        public int BuyerCharacterId { get; set; }
        public string BuyerName { get; set; }
        public double Amount { get; set; }
        public string Status { get; set; } = OfferStatus.Pending;
        public long CreatedAt { get; set; }
    }

    /// <summary>Argent dû à un vendeur qui n'était pas connecté au moment de la vente.</summary>
    public class OccazPayout : ModEntity<OccazPayout>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int CharacterId { get; set; }
        public double Amount { get; set; }
        public string Reason { get; set; }
        public bool Paid { get; set; }
        public long CreatedAt { get; set; }
    }

    // ----------------------------------------------------------------------
    //  Points bleus devant les voitures en vente
    // ----------------------------------------------------------------------

    /// <summary>
    /// Un point bleu natif (NCheckpoint) par annonce et par joueur connecté, placé devant la voiture.
    /// Marcher dessus ouvre l'annonce. Les points sont créés à la connexion et à chaque nouvelle annonce,
    /// et détruits à la vente ou au retrait.
    /// </summary>
    public static class OccazInfoPoints
    {
        private static readonly Dictionary<int, List<NCheckpoint>> Points = new Dictionary<int, List<NCheckpoint>>();

        private static OccazConfig Config => OccazPlugin.Instance.Config;

        private static IEnumerable<Player> OnlinePlayers() =>
            Nova.server.GetAllInGamePlayers().Where(p => p != null && p.character != null);

        private static void Create(Player player, int listingId, Vector3 position)
        {
            NCheckpoint checkpoint = new NCheckpoint(player.netId, position, _ => OccazMenus.OpenFromPoint(player, listingId));
            player.CreateCheckpoint(checkpoint);
            if (!Points.TryGetValue(listingId, out List<NCheckpoint> list))
                Points[listingId] = list = new List<NCheckpoint>();
            list.Add(checkpoint);
        }

        private static void Destroy(NCheckpoint checkpoint)
        {
            try { Nova.server.Players.FirstOrDefault(p => p != null && p.netId == checkpoint.playerId)?.DestroyCheckpoint(checkpoint); }
            catch (Exception ex) { Logger.LogWarning("Occaz points", ex.Message); }
        }

        /// <summary>Affiche le point d'une nouvelle annonce à tous les joueurs connectés.</summary>
        public static void ShowListing(OccazListing listing, OccazParking parking)
        {
            Hide(listing.Id);
            if (!Config.InfoPointEnabled || parking == null) return;
            foreach (Player player in OnlinePlayers())
                Create(player, listing.Id, parking.InfoPosition);
        }

        /// <summary>Affiche au joueur les points de toutes les annonces (à la connexion).</summary>
        public static async Task ShowFor(Player player)
        {
            try
            {
                foreach (List<NCheckpoint> list in Points.Values)
                {
                    foreach (NCheckpoint checkpoint in list.Where(c => c.playerId == player.netId).ToList())
                    {
                        Destroy(checkpoint);
                        list.Remove(checkpoint);
                    }
                }
                if (!Config.InfoPointEnabled) return;

                Dictionary<int, OccazParking> parkings = (await OccazParking.QueryAll()).ToDictionary(p => p.Id);
                foreach (OccazListing listing in await OccazService.ActiveListings())
                    if (parkings.TryGetValue(listing.ParkingId, out OccazParking parking))
                        Create(player, listing.Id, parking.InfoPosition);
            }
            catch (Exception ex)
            {
                Logger.LogError("Occaz points", ex.Message);
            }
        }

        public static void Hide(int listingId)
        {
            if (!Points.TryGetValue(listingId, out List<NCheckpoint> list)) return;
            foreach (NCheckpoint checkpoint in list) Destroy(checkpoint);
            Points.Remove(listingId);
        }

        /// <summary>Recrée tous les points (après un changement de config ou de place de parking).</summary>
        public static async void RefreshAll()
        {
            foreach (int id in Points.Keys.ToList()) Hide(id);
            foreach (Player player in OnlinePlayers()) await ShowFor(player);
        }
    }

    // ----------------------------------------------------------------------
    //  Logique métier
    // ----------------------------------------------------------------------

    public static class OccazService
    {
        private const string Title = OccazPlugin.Title;
        private static OccazConfig Config => OccazPlugin.Instance.Config;

        /// <summary>Ids (base de données) des véhicules actuellement en vente.</summary>
        private static readonly HashSet<int> ListedVehicles = new HashSet<int>();

        /// <summary>Annonces en cours de traitement (achat, retrait) pour éviter les doubles ventes.</summary>
        private static readonly HashSet<int> Busy = new HashSet<int>();

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string Money(double amount) => $"{amount:N0}€";

        public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dd/MM/yyyy HH:mm");

        public static string ModelName(int modelId)
        {
            try { return VehicleUtils.GetModelNameByModelId(modelId); }
            catch { return $"Modèle #{modelId}"; }
        }

        public static int Icon(int modelId)
        {
            try { return VehicleUtils.GetIconId(modelId); }
            catch { return ItemUtils.GetIconIdByItemId(1112); }
        }

        public static double CommissionOf(double price) => Math.Round(price * Config.CommissionPercent / 100.0);

        public static bool IsListed(int vehicleDbId) => ListedVehicles.Contains(vehicleDbId);

        public static Player FindOnline(int characterId) =>
            Nova.server.Players.FirstOrDefault(p => p != null && p.character != null && p.character.Id == characterId);

        public static async Task LoadCache()
        {
            ListedVehicles.Clear();
            foreach (OccazListing listing in await ActiveListings())
                ListedVehicles.Add(listing.VehicleDbId);
            RelockAll();
            OccazInfoPoints.RefreshAll();
        }

        public static Task<List<OccazListing>> ActiveListings() =>
            OccazListing.Query(l => l.Status == ListingStatus.Active);

        /// <summary>Reverrouille les voitures en vente (elles peuvent réapparaître déverrouillées après un redémarrage).</summary>
        public static void RelockAll()
        {
            foreach (int id in ListedVehicles)
            {
                Vehicle instance = Nova.v.GetVehicle(id)?.instance;
                if (instance != null && !instance.isLocked)
                    instance.NetworkisLocked = true;
            }
        }

        public static Vector3? VehiclePosition(LifeVehicle vehicle)
        {
            if (vehicle == null) return null;
            if (vehicle.instance != null) return vehicle.instance.transform.position;
            return new Vector3(vehicle.x, vehicle.y, vehicle.z);
        }

        public static int OwnerOf(LifeVehicle vehicle) => vehicle?.permissions?.owner?.characterId ?? 0;

        /// <summary>Véhicules du joueur, proches de lui, qui peuvent être mis en vente.</summary>
        public static List<LifeVehicle> SellableVehicles(Player player)
        {
            return Nova.v.GetOwnedVehiclesInRange(player.setup.transform.position, Config.SellerVehicleRange, player.character.Id)
                .Where(v => v != null && v.bizId == 0 && OwnerOf(v) == player.character.Id && !v.isStowed && v.instance != null
                            && !IsListed(v.vehicleId) && !Config.BlacklistedModelIds.Contains(v.modelId))
                .ToList();
        }

        public static async Task<OccazParking> FreeParking()
        {
            HashSet<int> used = new HashSet<int>((await ActiveListings()).Select(l => l.ParkingId));
            return (await OccazParking.QueryAll()).FirstOrDefault(p => !used.Contains(p.Id));
        }

        /// <summary>Place libre sur laquelle la voiture est déjà garée (mode « le vendeur se gare »).</summary>
        public static async Task<OccazParking> ParkingUnder(LifeVehicle vehicle)
        {
            Vector3? position = VehiclePosition(vehicle);
            if (position == null) return null;
            HashSet<int> used = new HashSet<int>((await ActiveListings()).Select(l => l.ParkingId));
            return (await OccazParking.QueryAll())
                .Where(p => !used.Contains(p.Id) && Vector3.Distance(p.Position, position.Value) <= Config.ParkingSpotRadius)
                .OrderBy(p => Vector3.Distance(p.Position, position.Value))
                .FirstOrDefault();
        }

        /// <summary>Range la voiture puis la fait réapparaître sur la place de parking.</summary>
        private static async Task<bool> MoveToParking(LifeVehicle vehicle, OccazParking parking)
        {
            vehicle.instance?.EjectAll();
            Nova.v.StowVehicle(vehicle.vehicleId);

            // La sauvegarde est asynchrone : on attend que l'ancienne instance soit détruite
            for (int i = 0; i < 40 && vehicle.instance != null; i++)
                await Task.Delay(250);
            if (vehicle.instance != null) return false;

            bool ok = Nova.v.UnstowVehicle(vehicle.vehicleId, parking.Position + Vector3.up * 0.5f, Quaternion.Euler(0f, parking.RotY, 0f));
            for (int i = 0; i < 20 && ok && vehicle.instance == null; i++)
                await Task.Delay(250);
            return ok;
        }

        /// <summary>Publie l'annonce : vérifications, frais, déplacement sur le parking, verrouillage, point bleu.</summary>
        public static async Task<bool> CreateListing(Player player, OccazListing draft)
        {
            if (Busy.Contains(-draft.VehicleDbId)) return false;
            Busy.Add(-draft.VehicleDbId);
            try
            {
                if (draft.Price <= 0 || draft.Mileage < 0 || string.IsNullOrEmpty(draft.Condition))
                    return Fail(player, "Remplissez le prix, le kilométrage et l'état.");

                LifeVehicle vehicle = Nova.v.GetVehicle(draft.VehicleDbId);
                if (vehicle == null || vehicle.instance == null || OwnerOf(vehicle) != player.character.Id || vehicle.bizId != 0)
                    return Fail(player, "Ce véhicule n'est plus disponible (rangé ou plus à vous).");
                if (IsListed(vehicle.vehicleId))
                    return Fail(player, "Ce véhicule est déjà en vente.");
                if (Config.BlacklistedModelIds.Contains(vehicle.modelId))
                    return Fail(player, "Ce modèle ne peut pas être vendu d'occasion.");
                if (draft.Price < Config.MinPrice || draft.Price > Config.MaxPrice)
                    return Fail(player, $"Le prix doit être entre {Money(Config.MinPrice)} et {Money(Config.MaxPrice)}.");

                int mine = (await OccazListing.Query(l => l.Status == ListingStatus.Active && l.SellerCharacterId == player.character.Id)).Count;
                if (mine >= Config.MaxListingsPerPlayer)
                    return Fail(player, $"Vous avez déjà {mine} annonce(s) en cours (maximum {Config.MaxListingsPerPlayer}).");

                if (Config.ListingFee > 0 && player.Money < Config.ListingFee)
                    return Fail(player, $"Il vous faut {Money(Config.ListingFee)} en espèces pour les frais d'annonce.");

                OccazParking parking = Config.TeleportToParking ? await FreeParking() : await ParkingUnder(vehicle);
                if (parking == null)
                    return Fail(player, Config.TeleportToParking
                        ? "Aucune place de parking libre, revenez plus tard."
                        : "Garez d'abord votre véhicule sur une place libre du parking Occaz.");

                if (Config.TeleportToParking && !await MoveToParking(vehicle, parking))
                    return Fail(player, "Déplacement impossible : le véhicule a été rangé, ressortez-le et réessayez.");

                if (Config.ListingFee > 0)
                    player.AddMoney(-Config.ListingFee, "Occaz : frais d'annonce");

                draft.ModelId = vehicle.modelId;
                draft.Plate = vehicle.plate;
                draft.SellerCharacterId = player.character.Id;
                draft.SellerName = player.FullName;
                draft.ParkingId = parking.Id;
                draft.Status = ListingStatus.Active;
                draft.CreatedAt = Now;
                draft.ExpiresAt = Now + Config.ListingDurationHours * 3600L;
                if (!await draft.Save())
                {
                    if (Config.ListingFee > 0) player.AddMoney(Config.ListingFee, "Occaz : remboursement");
                    return Fail(player, "Erreur lors de l'enregistrement de l'annonce.");
                }

                ListedVehicles.Add(vehicle.vehicleId);
                if (vehicle.instance != null) vehicle.instance.NetworkisLocked = true;
                OccazInfoPoints.ShowListing(draft, parking);

                player.Notify(Title, $"Votre {draft.Model} est en vente pour {Money(draft.Price)} sur la place « {parking.Name} ».",
                    NotificationManager.Type.Success, 8f);
                if (Config.AnnounceNewListings)
                    foreach (Player other in Nova.server.GetAllInGamePlayers().Where(p => p != null && p != player))
                        other.Notify(Title, $"Nouvelle occasion : {draft.Model} à {Money(draft.Price)} au parking Occaz !",
                            NotificationManager.Type.Info, 6f);

                Webhook("#4CAF50", "🚗 Nouvelle annonce", $"**{draft.Model}** est en vente au parking Occaz.", draft,
                    ("Place", parking.Name), ("Expire le", Date(draft.ExpiresAt)));
                return true;
            }
            finally
            {
                Busy.Remove(-draft.VehicleDbId);
            }
        }

        /// <summary>Achat d'une annonce (au prix affiché ou au prix d'une offre acceptée).</summary>
        public static async Task<bool> Buy(Player buyer, int listingId, bool useBank, OccazOffer acceptedOffer = null)
        {
            if (Busy.Contains(listingId))
                return Fail(buyer, "Cette annonce est en cours de traitement, réessayez.");
            Busy.Add(listingId);
            try
            {
                OccazListing listing = await OccazListing.Query(listingId);
                if (listing == null || listing.Status != ListingStatus.Active)
                    return Fail(buyer, "Cette annonce n'est plus disponible.");
                if (listing.SellerCharacterId == buyer.character.Id)
                    return Fail(buyer, "Vous ne pouvez pas acheter votre propre véhicule.");

                double price = listing.Price;
                if (acceptedOffer != null)
                {
                    OccazOffer offer = await OccazOffer.Query(acceptedOffer.Id);
                    if (offer == null || offer.Status != OfferStatus.Accepted || offer.ListingId != listing.Id || offer.BuyerCharacterId != buyer.character.Id)
                        return Fail(buyer, "Cette offre n'est plus valable.");
                    price = offer.Amount;
                }

                if (useBank && !Config.AllowBankPayment)
                    return Fail(buyer, "Le paiement par carte est désactivé.");
                if ((useBank ? buyer.Bank : buyer.Money) < price)
                    return Fail(buyer, $"Fonds insuffisants : il vous faut {Money(price)} {(useBank ? "en banque" : "en espèces")}.");

                LifeVehicle vehicle = Nova.v.GetVehicle(listing.VehicleDbId);
                if (vehicle == null || OwnerOf(vehicle) != listing.SellerCharacterId)
                {
                    await Close(listing, ListingStatus.Cancelled);
                    return Fail(buyer, "Ce véhicule n'est plus disponible, l'annonce a été retirée.");
                }

                // Paiement de l'acheteur
                if (useBank) buyer.AddBankMoney(-price, $"Occaz : achat {listing.Plate}");
                else buyer.AddMoney(-price, $"Occaz : achat {listing.Plate}");

                // Transfert de propriété : l'ancien propriétaire et les co-propriétaires perdent le véhicule
                vehicle.permissions.owner.characterId = buyer.character.Id;
                vehicle.permissions.owner.groupId = 0;
                vehicle.permissions.coOwners?.Clear();
                vehicle.coOwners = new string[0];
                vehicle.Save();

                double commission = CommissionOf(price);
                listing.BuyerCharacterId = buyer.character.Id;
                listing.BuyerName = buyer.FullName;
                listing.SoldPrice = price;
                listing.Commission = commission;
                await Close(listing, ListingStatus.Sold);

                await PaySeller(listing.SellerCharacterId, price - commission, $"Occaz : vente {listing.Plate}");

                buyer.Notify(Title, $"Félicitations ! Vous êtes le nouveau propriétaire du {listing.Model} ({listing.Plate}).",
                    NotificationManager.Type.Success, 8f);
                if (vehicle.instance != null) buyer.setup.TargetSetGPSTarget(vehicle.instance.transform.position);

                Webhook("#2196F3", "✅ Véhicule vendu", $"**{listing.Model}** a trouvé preneur.", listing,
                    ("Acheteur", buyer.FullName), ("Prix payé", Money(price)), ("Commission", Money(commission)),
                    ("Paiement", useBank ? "Carte" : "Espèces"));
                return true;
            }
            finally
            {
                Busy.Remove(listingId);
            }
        }

        private static async Task PaySeller(int characterId, double amount, string reason)
        {
            if (amount <= 0) return;
            Player seller = FindOnline(characterId);
            if (seller != null)
            {
                seller.AddBankMoney(amount, reason);
                seller.Notify(Title, $"Votre véhicule a été vendu ! {Money(amount)} versés sur votre compte bancaire.",
                    NotificationManager.Type.Success, 8f);
                return;
            }
            await new OccazPayout { CharacterId = characterId, Amount = amount, Reason = reason, CreatedAt = Now }.Save();
        }

        /// <summary>Verse au joueur l'argent des ventes faites pendant qu'il était déconnecté.</summary>
        public static async Task PayPendingPayouts(Player player)
        {
            if (player?.character == null) return;
            List<OccazPayout> payouts = await OccazPayout.Query(p => p.CharacterId == player.character.Id && !p.Paid);
            double total = 0;
            foreach (OccazPayout payout in payouts)
            {
                payout.Paid = true;
                if (!await payout.Save()) continue;
                player.AddBankMoney(payout.Amount, payout.Reason);
                total += payout.Amount;
            }
            if (total > 0)
                player.Notify(Title, $"Pendant votre absence, vos ventes Occaz vous ont rapporté {Money(total)} (en banque).",
                    NotificationManager.Type.Success, 10f);
        }

        /// <summary>Retire une annonce : la voiture reste sur le parking, déverrouillée, et appartient toujours au vendeur.</summary>
        public static async Task<bool> Cancel(Player by, OccazListing listing, string status, string reason)
        {
            if (Busy.Contains(listing.Id)) return false;
            Busy.Add(listing.Id);
            try
            {
                OccazListing fresh = await OccazListing.Query(listing.Id);
                if (fresh == null || fresh.Status != ListingStatus.Active) return false;
                await Close(fresh, status);

                Player seller = FindOnline(fresh.SellerCharacterId);
                if (seller != null && seller != by)
                    seller.Notify(Title, $"Votre annonce {fresh.Model} ({fresh.Plate}) a été retirée : {reason}. Votre véhicule vous attend au parking.",
                        NotificationManager.Type.Warning, 10f);

                Webhook(status == ListingStatus.Expired ? "#9E9E9E" : "#FF9800",
                    status == ListingStatus.Expired ? "⌛ Annonce expirée" : "❌ Annonce retirée",
                    $"**{fresh.Model}** n'est plus en vente : {reason}.", fresh);
                return true;
            }
            finally
            {
                Busy.Remove(listing.Id);
            }
        }

        private static async Task Close(OccazListing listing, string status)
        {
            listing.Status = status;
            listing.ClosedAt = Now;
            await listing.Save();
            ListedVehicles.Remove(listing.VehicleDbId);
            OccazInfoPoints.Hide(listing.Id);

            Vehicle instance = Nova.v.GetVehicle(listing.VehicleDbId)?.instance;
            if (instance != null) instance.NetworkisLocked = false;

            foreach (OccazOffer offer in await OccazOffer.Query(o => o.ListingId == listing.Id && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Accepted)))
            {
                offer.Status = OfferStatus.Closed;
                await offer.Save();
            }
        }

        /// <summary>Expiration des annonces et retrait de celles dont le véhicule a disparu ou changé de propriétaire.</summary>
        public static async Task CheckListings()
        {
            try
            {
                foreach (OccazListing listing in await ActiveListings())
                {
                    LifeVehicle vehicle = Nova.v.GetVehicle(listing.VehicleDbId);
                    if (vehicle == null || OwnerOf(vehicle) != listing.SellerCharacterId)
                        await Cancel(null, listing, ListingStatus.Cancelled, "véhicule introuvable ou propriétaire changé");
                    else if (listing.ExpiresAt > 0 && Now >= listing.ExpiresAt)
                        await Cancel(null, listing, ListingStatus.Expired, "durée de l'annonce dépassée");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(OccazPlugin.Instance.PluginInformations.SourceName, $"CheckListings : {ex.Message}");
            }
        }

        /// <summary>Repart pour une durée complète à partir de maintenant.</summary>
        public static async Task<bool> Renew(Player seller, OccazListing listing)
        {
            OccazListing fresh = await OccazListing.Query(listing.Id);
            if (fresh == null || fresh.Status != ListingStatus.Active) return Fail(seller, "Cette annonce n'est plus active.");
            fresh.ExpiresAt = Now + Config.ListingDurationHours * 3600L;
            if (!await fresh.Save()) return false;
            listing.ExpiresAt = fresh.ExpiresAt;
            seller.Notify(Title, $"Annonce prolongée jusqu'au {Date(fresh.ExpiresAt)}.", NotificationManager.Type.Success, 6f);
            return true;
        }

        public static async Task<bool> MakeOffer(Player buyer, OccazListing listing, double amount)
        {
            if (!Config.AllowOffers) return Fail(buyer, "Les offres sont désactivées.");
            if (listing.SellerCharacterId == buyer.character.Id) return Fail(buyer, "C'est votre propre annonce.");
            if (amount <= 0 || amount >= listing.Price) return Fail(buyer, $"L'offre doit être inférieure au prix affiché ({Money(listing.Price)}).");
            if (amount < Config.MinPrice) return Fail(buyer, $"L'offre minimum est de {Money(Config.MinPrice)}.");

            List<OccazOffer> existing = await OccazOffer.Query(o => o.ListingId == listing.Id && o.BuyerCharacterId == buyer.character.Id
                                                                 && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Accepted));
            foreach (OccazOffer old in existing)
            {
                old.Status = OfferStatus.Closed;
                await old.Save();
            }

            OccazOffer offer = new OccazOffer
            {
                ListingId = listing.Id,
                BuyerCharacterId = buyer.character.Id,
                BuyerName = buyer.FullName,
                Amount = amount,
                Status = OfferStatus.Pending,
                CreatedAt = Now
            };
            if (!await offer.Save()) return Fail(buyer, "Erreur lors de l'envoi de l'offre.");

            buyer.Notify(Title, $"Offre de {Money(amount)} envoyée au vendeur.", NotificationManager.Type.Success, 6f);
            FindOnline(listing.SellerCharacterId)?.Notify(Title,
                $"{buyer.FullName} vous propose {Money(amount)} pour votre {listing.Model}. Répondez au point Occaz > Mes annonces.",
                NotificationManager.Type.Info, 10f);
            return true;
        }

        public static async Task<bool> AnswerOffer(Player seller, OccazOffer offer, OccazListing listing, bool accept)
        {
            OccazOffer fresh = await OccazOffer.Query(offer.Id);
            if (fresh == null || fresh.Status != OfferStatus.Pending) return Fail(seller, "Cette offre n'est plus en attente.");
            fresh.Status = accept ? OfferStatus.Accepted : OfferStatus.Refused;
            if (!await fresh.Save()) return false;

            seller.Notify(Title, accept ? "Offre acceptée : l'acheteur peut maintenant acheter à ce prix." : "Offre refusée.",
                NotificationManager.Type.Success, 6f);
            FindOnline(fresh.BuyerCharacterId)?.Notify(Title, accept
                    ? $"Le vendeur accepte votre offre de {Money(fresh.Amount)} pour le {listing.Model} ! Achetez-le sur le point bleu devant la voiture."
                    : $"Le vendeur a refusé votre offre de {Money(fresh.Amount)} pour le {listing.Model}.",
                accept ? NotificationManager.Type.Success : NotificationManager.Type.Warning, 10f);
            return true;
        }

        public static async Task<bool> ChangePrice(Player seller, OccazListing listing, double price)
        {
            if (price < Config.MinPrice || price > Config.MaxPrice)
                return Fail(seller, $"Le prix doit être entre {Money(Config.MinPrice)} et {Money(Config.MaxPrice)}.");
            OccazListing fresh = await OccazListing.Query(listing.Id);
            if (fresh == null || fresh.Status != ListingStatus.Active) return Fail(seller, "Cette annonce n'est plus active.");
            double old = fresh.Price;
            fresh.Price = price;
            if (!await fresh.Save()) return false;
            listing.Price = price;
            seller.Notify(Title, $"Nouveau prix : {Money(price)}.", NotificationManager.Type.Success, 5f);
            if (price < old)
                Webhook("#9C27B0", "📉 Baisse de prix", $"**{fresh.Model}** passe de ~~{Money(old)}~~ à **{Money(price)}** !", fresh);
            return true;
        }

        public static bool Fail(Player player, string message)
        {
            player?.Notify(Title, message, NotificationManager.Type.Error, 6f);
            return false;
        }

        public static void Webhook(string color, string title, string description, OccazListing listing, params (string name, string value)[] extra)
        {
            string url = Config.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            List<string> names = new List<string> { "Modèle", "Plaque", "Prix", "Kilométrage", "État", "Vendeur" };
            List<string> values = new List<string>
            {
                $"{listing.Model} ({listing.RealModelName})", listing.Plate ?? "-", Money(listing.Price),
                $"{Math.Max(0, listing.Mileage):N0} km", listing.Condition ?? "-", listing.SellerName ?? "-"
            };
            if (!string.IsNullOrWhiteSpace(listing.Description))
            {
                names.Add("Description");
                values.Add(listing.Description);
            }
            foreach ((string name, string value) in extra)
            {
                names.Add(name);
                values.Add(value);
            }

            string footer = string.IsNullOrWhiteSpace(Config.WebhookFooter) ? OccazPlugin.Credit : Config.WebhookFooter;
            Task.Run(async () =>
            {
                try
                {
                    await new DiscordWebhookClient(url).SendEmbedAsync(color, title, description, names, values, true, true, footer);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Occaz webhook", ex.Message);
                }
            });
        }
    }

    // ----------------------------------------------------------------------
    //  Menus joueurs
    // ----------------------------------------------------------------------

    public static partial class OccazMenus
    {
        private const string T = OccazPlugin.Title;
        private static OccazPlugin Context => OccazPlugin.Instance;
        private static OccazConfig Config => OccazPlugin.Instance.Config;

        private static readonly string[] Conditions = { "Comme neuf", "Très bon état", "Bon état", "État correct", "À réparer", "Épave" };

        private static string M(double amount) => OccazService.Money(amount);

        private static string Todo => Color("à remplir", Colors.Error);

        /// <summary>Affiche le panneau avec la signature « Created by Loris Strange » en bas.</summary>
        public static void Show(Panel panel)
        {
            string credit = Color(OccazPlugin.Credit, Colors.Grey);
            switch (panel.type)
            {
                case UIPanel.PanelType.Tab:
                    panel.AddTabLine(credit, _ => { });
                    break;
                case UIPanel.PanelType.TabPrice:
                    panel.AddTabLine(credit, "", OccazService.Icon(0), _ => { });
                    break;
                default:
                    // Panneau texte : 6 lignes maximum, sinon le texte passe sous le titre
                    if (panel.TextLines.Count < 6) panel.TextLines.Add(credit);
                    break;
            }
            panel.Display();
        }

        // ---------- Point bleu vendeur ----------

        public static async void Main(Player player, string pointName)
        {
            List<OccazListing> active = await OccazService.ActiveListings();
            int mine = active.Count(l => l.SellerCharacterId == player.character.Id);
            List<OccazOffer> myOffers = await OccazOffer.Query(o => o.BuyerCharacterId == player.character.Id && o.Status == OfferStatus.Accepted);

            Panel panel = Context.PanelHelper.Create(pointName ?? "Occaz By Loris Strange", UIPanel.PanelType.Tab, player, () => Main(player, pointName));
            panel.AddTabLine(Bold("Vendre mon véhicule"), _ => SellChooseVehicle(player));
            panel.AddTabLine($"Catalogue des occasions ({active.Count})", _ => Catalogue(player));
            panel.AddTabLine($"Mes annonces ({mine})", _ => MyListings(player));
            if (Config.AllowOffers)
                panel.AddTabLine(myOffers.Count > 0 ? Color($"Mes offres ({myOffers.Count} acceptée(s))", Colors.Success) : "Mes offres", _ => MyOffers(player));
            panel.AddTabLine("Comment ça marche ?", _ => Help(player));
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            Show(panel);
        }

        private static void Help(Player player)
        {
            // Panneau texte du jeu : 6 lignes courtes maximum, sinon le texte passe sous le titre
            Panel panel = Context.PanelHelper.Create("Occaz - Aide", UIPanel.PanelType.Text, player, () => Help(player));
            panel.TextLines.Add(Bold("VENDRE") + " : garez-vous ici, puis Vendre.");
            panel.TextLines.Add($"Exposée et verrouillée {Config.ListingDurationHours} h max.");
            panel.TextLines.Add($"Frais {M(Config.ListingFee)} - commission {Config.CommissionPercent}%.");
            panel.TextLines.Add(Bold("ACHETER") + " : marchez sur le point bleu");
            panel.TextLines.Add("devant la voiture, ou le Catalogue.");
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Vente : un seul formulaire ----------

        private static void SellChooseVehicle(Player player)
        {
            List<LifeVehicle> vehicles = OccazService.SellableVehicles(player);
            if (vehicles.Count == 1)
            {
                // Une seule voiture possible : on passe directement au formulaire
                SellForm(player, NewDraft(vehicles[0]));
                return;
            }

            Panel panel = Context.PanelHelper.Create("Occaz - Quel véhicule vendre ?", UIPanel.PanelType.TabPrice, player, () => SellChooseVehicle(player));
            if (vehicles.Count == 0)
                panel.AddTabLine($"Garez votre véhicule à moins de {Config.SellerVehicleRange:0} m", "", OccazService.Icon(0), _ => { });
            foreach (LifeVehicle vehicle in vehicles)
            {
                LifeVehicle v = vehicle;
                panel.AddTabLine(OccazService.ModelName(v.modelId), v.plate, OccazService.Icon(v.modelId), _ => SellForm(player, NewDraft(v)));
            }
            if (vehicles.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static OccazListing NewDraft(LifeVehicle v) => new OccazListing
        {
            VehicleDbId = v.vehicleId,
            ModelId = v.modelId,
            Plate = v.plate,
            Model = OccazService.ModelName(v.modelId),
            Mileage = -1
        };

        /// <summary>Formulaire unique : on clique sur une ligne pour la remplir, puis « Publier ».</summary>
        private static void SellForm(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Mon annonce", UIPanel.PanelType.TabPrice, player, () => SellForm(player, draft));
            int icon = OccazService.Icon(draft.ModelId);
            panel.AddTabLine("Véhicule", draft.Plate, icon, _ => { });
            panel.AddTabLine("Modèle affiché", draft.Model, icon, _ => EditText(player, "Modèle affiché", draft.RealModelName, 40, v => draft.Model = string.IsNullOrEmpty(v) ? draft.RealModelName : v));
            panel.AddTabLine("Prix", draft.Price > 0 ? Color(Bold(M(draft.Price)), Colors.Success) : Todo, icon,
                _ => EditMoney(player, "Prix de vente", $"{M(Config.MinPrice)} à {M(Config.MaxPrice)}", Config.MinPrice, Config.MaxPrice, v => draft.Price = v));
            panel.AddTabLine("Kilométrage", draft.Mileage >= 0 ? $"{draft.Mileage:N0} km" : Todo, icon,
                _ => EditMoney(player, "Kilométrage (km)", "0 à 2 000 000", 0, 2000000, v => draft.Mileage = (int)v, allowZero: true));
            panel.AddTabLine("État", draft.Condition ?? Todo, icon, _ => ChooseCondition(player, draft));
            panel.AddTabLine("Description", string.IsNullOrWhiteSpace(draft.Description) ? Color("facultatif", Colors.Grey) : Short(draft.Description, 22), icon,
                _ => EditText(player, "Description", "Vidange faite, première main...", 200, v => draft.Description = v));
            if (Config.ListingFee > 0)
                panel.AddTabLine("Frais d'annonce (espèces)", M(Config.ListingFee), icon, _ => { });
            if (draft.Price > 0)
                panel.AddTabLine($"Vous toucherez (commission {Config.CommissionPercent}%)", Color(Bold(M(draft.Price - OccazService.CommissionOf(draft.Price))), Colors.Success), icon, _ => { });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.CloseButtonWithAction(Color("Publier", Colors.Success), async () => await OccazService.CreateListing(player, draft));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void ChooseCondition(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - État du véhicule", UIPanel.PanelType.Tab, player, () => ChooseCondition(player, draft));
            foreach (string condition in Conditions)
            {
                string c = condition;
                panel.AddTabLine(c, _ =>
                {
                    draft.Condition = c;
                    panel.Previous();
                });
            }
            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Saisie de texte générique, revient au menu précédent après validation.</summary>
        private static void EditText(Player player, string label, string placeholder, int max, Action<string> apply)
        {
            Panel panel = Context.PanelHelper.Create($"Occaz - {label}", UIPanel.PanelType.Input, player, () => EditText(player, label, placeholder, max, apply));
            panel.TextLines.Add(label);
            panel.SetInputPlaceholder(placeholder);
            panel.PreviousButtonWithAction("Valider", () =>
            {
                apply(Limit(panel.inputText?.Trim() ?? "", max));
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Saisie d'un nombre (prix, km...) avec bornes.</summary>
        private static void EditMoney(Player player, string label, string range, double min, double max, Action<double> apply, bool allowZero = false)
        {
            Panel panel = Context.PanelHelper.Create($"Occaz - {label}", UIPanel.PanelType.Input, player, () => EditMoney(player, label, range, min, max, apply, allowZero));
            panel.TextLines.Add(label);
            panel.TextLines.Add(Color(range, Colors.Grey));
            panel.SetInputPlaceholder(range);
            panel.PreviousButtonWithAction("Valider", () =>
            {
                if (!TryParseNumber(panel.inputText, out double value, allowZero) || value < min || value > max)
                    return Task.FromResult(OccazService.Fail(player, $"Valeur invalide ({range})."));
                apply(value);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Achat ----------

        /// <summary>Le joueur marche sur le point bleu devant une voiture en vente.</summary>
        public static async void OpenFromPoint(Player player, int listingId)
        {
            if (player?.character == null) return;
            OccazListing listing = await OccazListing.Query(listingId);
            if (listing == null || listing.Status != ListingStatus.Active)
            {
                player.Notify(T, "Ce véhicule n'est plus en vente.", NotificationManager.Type.Info, 4f);
                return;
            }
            if (listing.SellerCharacterId == player.character.Id) ManageListing(player, listing);
            else Details(player, listing);
        }

        /// <summary>AAMenu → Interactions : annonce du véhicule en vente le plus proche.</summary>
        public static async void NearbyListing(Player player)
        {
            if (player == null) return;
            if (!Config.InteractionMenuEnabled)
            {
                player.Notify(T, "Marchez sur le point bleu devant la voiture pour voir l'annonce.", NotificationManager.Type.Info, 5f);
                return;
            }
            Vector3 position = player.setup.transform.position;
            OccazListing nearest = null;
            float best = Config.ProximityRange;
            foreach (OccazListing listing in await OccazService.ActiveListings())
            {
                Vector3? vehiclePosition = OccazService.VehiclePosition(Nova.v.GetVehicle(listing.VehicleDbId));
                if (vehiclePosition == null) continue;
                float distance = Vector3.Distance(position, vehiclePosition.Value);
                if (distance <= best)
                {
                    best = distance;
                    nearest = listing;
                }
            }

            if (nearest == null)
            {
                player.Notify(T, $"Aucun véhicule en vente à moins de {Config.ProximityRange:0} m.", NotificationManager.Type.Info, 5f);
                return;
            }
            Details(player, nearest);
        }

        public static async void Catalogue(Player player)
        {
            List<OccazListing> listings = (await OccazService.ActiveListings()).OrderBy(l => l.Price).ToList();
            Dictionary<int, string> parkings = (await OccazParking.QueryAll()).ToDictionary(p => p.Id, p => p.Name);

            Panel panel = Context.PanelHelper.Create($"Occaz - Catalogue ({listings.Count})", UIPanel.PanelType.TabPrice, player, () => Catalogue(player));
            if (listings.Count == 0)
                panel.AddTabLine("Aucun véhicule en vente pour le moment", "", OccazService.Icon(0), _ => { });
            foreach (OccazListing listing in listings)
            {
                OccazListing l = listing;
                string place = parkings.TryGetValue(l.ParkingId, out string name) ? name : "?";
                panel.AddTabLine($"{l.Model} - {Math.Max(0, l.Mileage):N0} km - {place}", M(l.Price), OccazService.Icon(l.ModelId), _ => Details(player, l));
            }
            if (listings.Count > 0) panel.NextButton("Voir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Ligne « nom | valeur » d'un panneau TabPrice (non cliquable).</summary>
        private static void Info(Panel panel, OccazListing l, string name, string value)
        {
            panel.AddTabLine(name, value, OccazService.Icon(l.ModelId), _ => { });
        }

        /// <summary>Fiche de l'annonce, une information par ligne (le panneau texte est trop petit).</summary>
        private static void AddListingLines(Panel panel, OccazListing l)
        {
            Info(panel, l, Bold(l.Model), Color(Bold(M(l.Price)), Colors.Success));
            if (l.RealModelName != l.Model)
                Info(panel, l, "Modèle officiel", l.RealModelName);
            Info(panel, l, "Kilométrage", $"{Math.Max(0, l.Mileage):N0} km");
            Info(panel, l, "État", l.Condition);
            Info(panel, l, "Plaque", l.Plate);
            if (!string.IsNullOrEmpty(l.SellerName))
                Info(panel, l, "Vendeur", l.SellerName);
            if (l.ExpiresAt > 0)
                Info(panel, l, "Fin de l'annonce", OccazService.Date(l.ExpiresAt));
            if (!string.IsNullOrWhiteSpace(l.Description))
                Info(panel, l, "Description", Short(l.Description, 25));
        }

        private static void Description(Player player, OccazListing l)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Description", UIPanel.PanelType.Text, player, () => Description(player, l));
            panel.TextLines.Add(Bold(l.Model));
            panel.TextLines.Add(l.Description);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        public static async void Details(Player player, OccazListing listing)
        {
            bool isSeller = listing.SellerCharacterId == player.character.Id;
            OccazOffer accepted = isSeller ? null : (await OccazOffer.Query(o => o.ListingId == listing.Id && o.BuyerCharacterId == player.character.Id
                                                                                 && o.Status == OfferStatus.Accepted)).FirstOrDefault();

            Panel panel = Context.PanelHelper.Create($"Occaz - {listing.Model}", UIPanel.PanelType.TabPrice, player, () => Details(player, listing));
            AddListingLines(panel, listing);
            if (accepted != null)
                Info(panel, listing, Color("Votre offre acceptée", Colors.Success), Color(Bold(M(accepted.Amount)), Colors.Success));

            if (isSeller)
            {
                panel.NextButton("Gérer", () => ManageListing(player, listing));
            }
            else
            {
                panel.NextButton(accepted != null ? $"Acheter {M(accepted.Amount)}" : "Acheter", () => ConfirmBuy(player, listing, accepted));
                if (Config.AllowOffers && accepted == null) panel.NextButton("Faire une offre", () => Offer(player, listing));
            }
            if (!string.IsNullOrWhiteSpace(listing.Description))
                panel.NextButton("Description", () => Description(player, listing));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void Locate(Player player, OccazListing listing)
        {
            Vector3? position = OccazService.VehiclePosition(Nova.v.GetVehicle(listing.VehicleDbId));
            if (position == null)
            {
                OccazService.Fail(player, "Véhicule introuvable.");
                return;
            }
            player.setup.TargetSetGPSTarget(position.Value);
            player.Notify(T, "Position du véhicule ajoutée à votre GPS.", NotificationManager.Type.Info, 5f);
        }

        private static void ConfirmBuy(Player player, OccazListing listing, OccazOffer offer)
        {
            double price = offer?.Amount ?? listing.Price;

            Panel panel = Context.PanelHelper.Create("Occaz - Confirmer l'achat", UIPanel.PanelType.Text, player, () => ConfirmBuy(player, listing, offer));
            panel.TextLines.Add($"{Bold(listing.Model)} - {listing.Plate}");
            panel.TextLines.Add($"Prix : {Color(Bold(M(price)), Colors.Success)}");
            panel.TextLines.Add($"Espèces : {M(player.Money)}");
            panel.TextLines.Add($"Banque : {M(player.Bank)}");
            panel.TextLines.Add(Color("Achat définitif.", Colors.Warning));

            panel.CloseButtonWithAction("Payer en espèces", async () => await OccazService.Buy(player, listing.Id, false, offer));
            if (Config.AllowBankPayment)
                panel.CloseButtonWithAction("Payer par carte", async () => await OccazService.Buy(player, listing.Id, true, offer));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void Offer(Player player, OccazListing listing)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Faire une offre", UIPanel.PanelType.Input, player, () => Offer(player, listing));
            panel.TextLines.Add($"Prix affiché : {M(listing.Price)}");
            panel.TextLines.Add("Votre offre :");
            panel.SetInputPlaceholder(M(Math.Round(listing.Price * 0.9)));
            panel.PreviousButtonWithAction("Envoyer", async () =>
                TryParseNumber(panel.inputText, out double amount)
                    ? await OccazService.MakeOffer(player, listing, amount)
                    : OccazService.Fail(player, "Montant invalide."));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async void MyOffers(Player player)
        {
            List<OccazOffer> offers = (await OccazOffer.Query(o => o.BuyerCharacterId == player.character.Id))
                .OrderByDescending(o => o.CreatedAt).Take(20).ToList();

            Panel panel = Context.PanelHelper.Create("Occaz - Mes offres", UIPanel.PanelType.TabPrice, player, () => MyOffers(player));
            if (offers.Count == 0)
                panel.AddTabLine("Vous n'avez fait aucune offre", "", OccazService.Icon(0), _ => { });
            foreach (OccazOffer offer in offers)
            {
                OccazOffer o = offer;
                OccazListing listing = await OccazListing.Query(o.ListingId);
                if (listing == null) continue;
                string status = o.Status switch
                {
                    OfferStatus.Accepted => Color("Acceptée - acheter", Colors.Success),
                    OfferStatus.Refused => Color("Refusée", Colors.Error),
                    OfferStatus.Pending => Color("En attente", Colors.Warning),
                    _ => Color("Terminée", Colors.Grey)
                };
                panel.AddTabLine($"{listing.Model} - {status}", M(o.Amount), OccazService.Icon(listing.ModelId), _ =>
                {
                    if (o.Status == OfferStatus.Accepted && listing.Status == ListingStatus.Active)
                        ConfirmBuy(player, listing, o);
                    else if (listing.Status == ListingStatus.Active)
                        Details(player, listing);
                });
            }
            if (offers.Count > 0) panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Gestion de ses annonces ----------

        private static async void MyListings(Player player)
        {
            List<OccazListing> listings = (await OccazListing.Query(l => l.SellerCharacterId == player.character.Id))
                .OrderByDescending(l => l.CreatedAt).Take(20).ToList();

            Panel panel = Context.PanelHelper.Create("Occaz - Mes annonces", UIPanel.PanelType.TabPrice, player, () => MyListings(player));
            if (listings.Count == 0)
                panel.AddTabLine("Vous n'avez aucune annonce", "", OccazService.Icon(0), _ => { });
            foreach (OccazListing listing in listings)
            {
                OccazListing l = listing;
                string status = l.Status switch
                {
                    ListingStatus.Active => Color("En vente", Colors.Success),
                    ListingStatus.Sold => Color($"Vendu à {l.BuyerName}", Colors.Info),
                    ListingStatus.Expired => Color("Expirée", Colors.Grey),
                    _ => Color("Retirée", Colors.Grey)
                };
                panel.AddTabLine($"{l.Model} ({l.Plate}) - {status}", M(l.Status == ListingStatus.Sold ? l.SoldPrice : l.Price),
                    OccazService.Icon(l.ModelId), _ =>
                    {
                        if (l.Status == ListingStatus.Active) ManageListing(player, l);
                    });
            }
            if (listings.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async void ManageListing(Player player, OccazListing listing)
        {
            List<OccazOffer> pending = await OccazOffer.Query(o => o.ListingId == listing.Id && o.Status == OfferStatus.Pending);

            Panel panel = Context.PanelHelper.Create($"Occaz - Gérer : {listing.Model}", UIPanel.PanelType.Tab, player, () => ManageListing(player, listing));
            panel.AddTabLine($"Changer le prix (actuel : {M(listing.Price)})", _ => EditPrice(player, listing));
            if (Config.AllowOffers)
                panel.AddTabLine(pending.Count > 0 ? Color($"Offres reçues ({pending.Count})", Colors.Warning) : "Offres reçues (0)", _ => Offers(player, listing));
            panel.AddTabLine($"Prolonger l'annonce (fin : {OccazService.Date(listing.ExpiresAt)})", async _ =>
            {
                await OccazService.Renew(player, listing);
                panel.Refresh();
            });
            panel.AddTabLine("Localiser mon véhicule", _ => Locate(player, listing));
            panel.AddTabLine(Color("Retirer de la vente", Colors.Error), _ => ConfirmCancel(player, listing));
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void EditPrice(Player player, OccazListing listing)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Nouveau prix", UIPanel.PanelType.Input, player, () => EditPrice(player, listing));
            panel.TextLines.Add($"Prix actuel : {M(listing.Price)}");
            panel.SetInputPlaceholder(M(listing.Price));
            panel.PreviousButtonWithAction("Valider", async () =>
                TryParseNumber(panel.inputText, out double price)
                    ? await OccazService.ChangePrice(player, listing, price)
                    : OccazService.Fail(player, "Prix invalide."));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async void Offers(Player player, OccazListing listing)
        {
            List<OccazOffer> offers = (await OccazOffer.Query(o => o.ListingId == listing.Id && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Accepted)))
                .OrderByDescending(o => o.Amount).ToList();
            bool accept = true;

            Panel panel = Context.PanelHelper.Create("Occaz - Offres reçues", UIPanel.PanelType.TabPrice, player, () => Offers(player, listing));
            if (offers.Count == 0)
                panel.AddTabLine("Aucune offre pour le moment", "", OccazService.Icon(0), _ => { });
            foreach (OccazOffer offer in offers)
            {
                OccazOffer o = offer;
                string label = o.Status == OfferStatus.Accepted ? $"{o.BuyerName} - {Color("acceptée", Colors.Success)}" : o.BuyerName;
                panel.AddTabLine(label, M(o.Amount), OccazService.Icon(listing.ModelId), async _ =>
                {
                    if (o.Status != OfferStatus.Pending) return;
                    await OccazService.AnswerOffer(player, o, listing, accept);
                    panel.Refresh();
                });
            }
            if (offers.Count > 0)
            {
                panel.AddButton(Color("Accepter", Colors.Success), _ => { accept = true; panel.SelectTab(); });
                panel.AddButton(Color("Refuser", Colors.Error), _ => { accept = false; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void ConfirmCancel(Player player, OccazListing listing)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Retirer l'annonce", UIPanel.PanelType.Text, player, () => ConfirmCancel(player, listing));
            panel.TextLines.Add($"Retirer {Bold(listing.Model)} de la vente ?");
            panel.TextLines.Add(Color("Frais d'annonce non remboursés.", Colors.Warning));
            panel.CloseButtonWithAction(Color("Retirer", Colors.Error), async () =>
            {
                bool ok = await OccazService.Cancel(player, listing, ListingStatus.Cancelled, "retirée par le vendeur");
                if (ok)
                {
                    player.Notify(T, "Annonce retirée. Votre véhicule vous attend sur le parking (ajouté au GPS).", NotificationManager.Type.Success, 6f);
                    Locate(player, listing);
                }
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Utilitaires ----------

        private static bool TryParseNumber(string text, out double value, bool allowZero = false)
        {
            string clean = (text ?? "").Replace(" ", "").Replace("€", "").Replace("km", "").Replace(" ", "").Replace(" ", "").Replace(",", ".");
            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && (allowZero ? value >= 0 : value > 0) && !double.IsInfinity(value) && !double.IsNaN(value);
        }

        private static string Limit(string text, int max) => text.Length <= max ? text : text.Substring(0, max);

        private static string Short(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    // ----------------------------------------------------------------------
    //  Menus staff : assistant d'installation, configuration en direct, gestion
    // ----------------------------------------------------------------------

    public static partial class OccazMenus
    {
        private static string Done => Color("FAIT", Colors.Success);
        private static string NotDone => Color("A FAIRE", Colors.Error);

        private static bool CheckAdmin(Player player)
        {
            if (player != null && player.IsAdmin && player.serviceAdmin && (player.account?.AdminLevel ?? 0) >= Config.AdminLevel) return true;
            OccazService.Fail(player, $"Réservé au staff en service (niveau admin {Config.AdminLevel} minimum).");
            return false;
        }

        private static Task<List<NPoint>> SellerPoints() => ModEntity<NPoint>.Query(p => p.TypeName == nameof(OccazPattern));

        public static async void AdminMain(Player player)
        {
            if (!CheckAdmin(player)) return;
            int parkings = (await OccazParking.QueryAll()).Count;
            int active = (await OccazService.ActiveListings()).Count;
            int steps = await SetupDoneCount();

            Panel panel = Context.PanelHelper.Create("Occaz By Loris Strange - Staff", UIPanel.PanelType.Tab, player, () => AdminMain(player));
            panel.AddTabLine(steps < 2 ? Color($"Installation pas à pas ({steps}/2 obligatoires)", Colors.Warning) : $"Installation pas à pas ({Color("prêt", Colors.Success)})", _ => Setup(player));
            panel.AddTabLine("Configuration (sans redémarrer)", _ => ConfigCategories(player));
            panel.AddTabLine($"Places de parking ({parkings})", _ => AdminParkings(player));
            panel.AddTabLine($"Annonces en cours ({active})", _ => AdminListings(player));
            panel.AddTabLine("Statistiques et historique", _ => AdminHistory(player));
            panel.AddTabLine("Points bleus vendeur", _ => new OccazPattern(false) { Context = Context }.CreateOrGenerate(player));
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Assistant d'installation ----------

        private static async Task<int> SetupDoneCount()
        {
            int done = 0;
            if ((await SellerPoints()).Count > 0) done++;
            if ((await OccazParking.QueryAll()).Count > 0) done++;
            return done;
        }

        private static async void Setup(Player player)
        {
            if (!CheckAdmin(player)) return;
            int points = (await SellerPoints()).Count;
            int parkings = (await OccazParking.QueryAll()).Count;
            bool webhook = !string.IsNullOrWhiteSpace(Config.WebhookUrl);
            int icon = OccazService.Icon(0);

            Panel panel = Context.PanelHelper.Create("Occaz - Installation pas à pas", UIPanel.PanelType.TabPrice, player, () => Setup(player));
            panel.AddTabLine("1. Point vendeur : placez-vous et cliquez", points > 0 ? $"{Done} ({points})" : NotDone, icon, async _ =>
            {
                await PlaceSellerPoint(player);
                panel.Refresh();
            });
            panel.AddTabLine("2. Places de parking (une par voiture)", parkings > 0 ? $"{Done} ({parkings})" : NotDone, icon, _ => AdminAddParking(player));
            panel.AddTabLine("3. Prix, frais et commission", $"{Config.CommissionPercent}% / {M(Config.ListingFee)}", icon, _ => ConfigCategory(player, SettingCategory.Sales));
            panel.AddTabLine("4. Webhook Discord (facultatif)", webhook ? Done : Color("non", Colors.Grey), icon,
                _ => EditSetting(player, typeof(OccazConfig).GetProperty(nameof(OccazConfig.WebhookUrl))));
            panel.AddTabLine("5. Vérifier l'installation", points > 0 && parkings > 0 ? Color("prêt", Colors.Success) : Color("incomplet", Colors.Warning), icon, _ => SetupCheck(player));
            panel.NextButton("Faire l'étape", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Place un point vendeur à la position du staff (crée le modèle de point si besoin).</summary>
        private static async Task PlaceSellerPoint(Player player)
        {
            OccazPattern pattern = (await OccazPattern.QueryAll()).FirstOrDefault();
            if (pattern == null)
            {
                pattern = new OccazPattern(false) { PatternName = "Occaz By Loris Strange" };
                if (!await pattern.Save())
                {
                    OccazService.Fail(player, "Erreur lors de la création du point.");
                    return;
                }
                pattern = (await OccazPattern.QueryAll()).FirstOrDefault() ?? pattern;
            }
            pattern.TypeName = nameof(OccazPattern);
            pattern.Context = Context;
            if (await Context.PointHelper.CreateNPoint(player, pattern))
                player.Notify(T, "Point vendeur placé à votre position.", NotificationManager.Type.Success, 5f);
            else
                OccazService.Fail(player, "Erreur lors de la création du point.");
        }

        private static async void SetupCheck(Player player)
        {
            int points = (await SellerPoints()).Count;
            List<OccazParking> parkings = await OccazParking.QueryAll();
            int free = parkings.Count - (await OccazService.ActiveListings()).Count;

            Panel panel = Context.PanelHelper.Create("Occaz - Vérification", UIPanel.PanelType.Text, player, () => SetupCheck(player));
            panel.TextLines.Add($"Points vendeur : {(points > 0 ? Color(points.ToString(), Colors.Success) : Color("aucun", Colors.Error))}");
            panel.TextLines.Add($"Places : {parkings.Count} (libres : {Math.Max(0, free)})");
            panel.TextLines.Add($"Discord : {(string.IsNullOrWhiteSpace(Config.WebhookUrl) ? "désactivé" : Color("activé", Colors.Success))}");
            panel.TextLines.Add(points > 0 && parkings.Count > 0
                ? Color(Bold("Tout est prêt, bonnes ventes !"), Colors.Success)
                : Color("Faites les étapes 1 et 2.", Colors.Warning));
            if (!string.IsNullOrWhiteSpace(Config.WebhookUrl))
                panel.AddButton("Tester Discord", _ => TestWebhook(player));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void TestWebhook(Player player)
        {
            if (string.IsNullOrWhiteSpace(Config.WebhookUrl))
            {
                OccazService.Fail(player, "Aucune URL de webhook configurée.");
                return;
            }
            OccazService.Webhook("#607D8B", "🔧 Test du webhook", $"Test envoyé par {player.FullName}.",
                new OccazListing { Model = "Exemple", ModelId = 0, Plate = "AA-000-AA", Price = 12345, Mileage = 50000, Condition = "Bon état", SellerName = player.FullName });
            player.Notify(T, "Message de test envoyé sur Discord.", NotificationManager.Type.Success, 5f);
        }

        // ---------- Configuration en direct ----------

        private static void ConfigCategories(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = Context.PanelHelper.Create("Occaz - Configuration", UIPanel.PanelType.Tab, player, () => ConfigCategories(player));
            foreach (string category in SettingCategory.All)
            {
                string c = category;
                panel.AddTabLine(c, _ => ConfigCategory(player, c));
            }
            panel.AddTabLine(Color("Recharger config.json", Colors.Grey), _ =>
            {
                Context.LoadConfig();
                OccazInfoPoints.RefreshAll();
                player.Notify(T, "config.json rechargé.", NotificationManager.Type.Success, 5f);
            });
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static string FormatSetting(PropertyInfo property)
        {
            object value = property.GetValue(Config);
            return value switch
            {
                bool b => b ? Color("OUI", Colors.Success) : Color("NON", Colors.Error),
                double d => d.ToString("0.##", CultureInfo.InvariantCulture),
                float f => f.ToString("0.##", CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                string s => string.IsNullOrEmpty(s) ? Color("vide", Colors.Grey) : Short(s, 18),
                List<int> list => $"{list.Count} modèle(s)",
                _ => value?.ToString() ?? ""
            };
        }

        private static void ConfigCategory(Player player, string category)
        {
            if (!CheckAdmin(player)) return;
            List<PropertyInfo> settings = OccazConfig.Settings().Where(p => p.GetCustomAttribute<SettingAttribute>().Category == category).ToList();

            Panel panel = Context.PanelHelper.Create($"Occaz - {category}", UIPanel.PanelType.TabPrice, player, () => ConfigCategory(player, category));
            foreach (PropertyInfo setting in settings)
            {
                PropertyInfo s = setting;
                panel.AddTabLine(s.GetCustomAttribute<SettingAttribute>().Label, FormatSetting(s), OccazService.Icon(0), _ =>
                {
                    if (s.PropertyType == typeof(bool))
                    {
                        // Interrupteur : bascule immédiatement
                        s.SetValue(Config, !(bool)s.GetValue(Config));
                        ApplyConfig(player, s);
                        panel.Refresh();
                    }
                    else
                    {
                        EditSetting(player, s);
                    }
                });
            }
            if (category == SettingCategory.Discord && !string.IsNullOrWhiteSpace(Config.WebhookUrl))
                panel.AddButton("Tester", _ => TestWebhook(player));
            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Enregistre config.json et applique tout de suite le changement.</summary>
        private static void ApplyConfig(Player player, PropertyInfo changed)
        {
            Config.Normalize();
            Context.SaveConfig();
            if (changed.Name == nameof(OccazConfig.InfoPointEnabled) || changed.Name == nameof(OccazConfig.InfoPointDistance))
                OccazInfoPoints.RefreshAll();
            player.Notify(T, $"{changed.GetCustomAttribute<SettingAttribute>().Label} : {Nova.RemoveTextFormat(FormatSetting(changed))} (appliqué)",
                NotificationManager.Type.Success, 4f);
        }

        private static void EditSetting(Player player, PropertyInfo setting)
        {
            if (!CheckAdmin(player)) return;
            if (setting.PropertyType == typeof(List<int>))
            {
                EditBlacklist(player);
                return;
            }

            SettingAttribute info = setting.GetCustomAttribute<SettingAttribute>();
            bool isText = setting.PropertyType == typeof(string);

            Panel panel = Context.PanelHelper.Create($"Occaz - {info.Label}", UIPanel.PanelType.Input, player, () => EditSetting(player, setting));
            panel.TextLines.Add(info.Help);
            panel.TextLines.Add($"Actuel : {FormatSetting(setting)}");
            if (!isText)
                panel.TextLines.Add(Color($"Entre {info.Min:0.##} et {info.Max:0.##}", Colors.Grey));
            panel.SetInputPlaceholder(isText ? (string)setting.GetValue(Config) : FormatSetting(setting));

            panel.PreviousButtonWithAction("Valider", () =>
            {
                string input = panel.inputText?.Trim() ?? "";
                if (isText)
                {
                    if (setting.Name == nameof(OccazConfig.WebhookUrl) && input.Length > 0 && !input.StartsWith("https://"))
                        return Task.FromResult(OccazService.Fail(player, "L'URL doit commencer par https://"));
                    setting.SetValue(Config, input);
                }
                else
                {
                    if (!TryParseNumber(input, out double value, allowZero: true) || value < info.Min || value > info.Max)
                        return Task.FromResult(OccazService.Fail(player, $"Valeur invalide (entre {info.Min:0.##} et {info.Max:0.##})."));
                    if (setting.PropertyType == typeof(int)) setting.SetValue(Config, (int)Math.Round(value));
                    else if (setting.PropertyType == typeof(float)) setting.SetValue(Config, (float)value);
                    else setting.SetValue(Config, value);
                }
                ApplyConfig(player, setting);
                return Task.FromResult(true);
            });
            if (isText)
                panel.PreviousButtonWithAction(Color("Vider", Colors.Error), () =>
                {
                    setting.SetValue(Config, "");
                    ApplyConfig(player, setting);
                    return Task.FromResult(true);
                });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Modèles interdits : ajout du véhicule le plus proche ou par id, retrait d'un clic.</summary>
        private static void EditBlacklist(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Modèles interdits", UIPanel.PanelType.TabPrice, player, () => EditBlacklist(player));
            if (Config.BlacklistedModelIds.Count == 0)
                panel.AddTabLine("Aucun modèle interdit", "", OccazService.Icon(0), _ => { });
            foreach (int modelId in Config.BlacklistedModelIds.ToList())
            {
                int id = modelId;
                panel.AddTabLine($"{OccazService.ModelName(id)} (#{id})", Color("retirer", Colors.Error), OccazService.Icon(id), _ =>
                {
                    Config.BlacklistedModelIds.Remove(id);
                    Context.SaveConfig();
                    player.Notify(T, $"{OccazService.ModelName(id)} peut de nouveau être vendu.", NotificationManager.Type.Success, 4f);
                    panel.Refresh();
                });
            }
            panel.AddButton("Ajouter véhicule proche", _ =>
            {
                Vehicle closest = VehicleUtils.GetClosestVehicle(player, 8);
                LifeVehicle vehicle = closest != null ? Nova.v.GetVehicle(closest.VehicleDbId) : null;
                if (vehicle == null)
                {
                    OccazService.Fail(player, "Aucun véhicule à moins de 8 m.");
                    return;
                }
                if (!Config.BlacklistedModelIds.Contains(vehicle.modelId))
                {
                    Config.BlacklistedModelIds.Add(vehicle.modelId);
                    Context.SaveConfig();
                }
                player.Notify(T, $"{OccazService.ModelName(vehicle.modelId)} interdit à la vente.", NotificationManager.Type.Success, 4f);
                panel.Refresh();
            });
            panel.NextButton("Ajouter par id", () => EditMoney(player, "Id du modèle", "ex. 12", 0, 100000, v =>
            {
                if (!Config.BlacklistedModelIds.Contains((int)v)) Config.BlacklistedModelIds.Add((int)v);
                Context.SaveConfig();
            }, allowZero: true));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ---------- Places de parking ----------

        private static async void AdminParkings(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<OccazParking> parkings = await OccazParking.QueryAll();
            HashSet<int> used = new HashSet<int>((await OccazService.ActiveListings()).Select(l => l.ParkingId));

            Panel panel = Context.PanelHelper.Create("Occaz - Places de parking", UIPanel.PanelType.Tab, player, () => AdminParkings(player));
            if (parkings.Count == 0)
                panel.AddTabLine("Aucune place : placez-vous sur une place puis « Ajouter ici »", _ => { });
            foreach (OccazParking parking in parkings)
            {
                OccazParking p = parking;
                panel.AddTabLine($"#{p.Id} {p.Name} - {(used.Contains(p.Id) ? Color("occupée", Colors.Warning) : Color("libre", Colors.Success))}", _ => AdminParking(player, p));
            }
            panel.NextButton("Ajouter ici", () => AdminAddParking(player));
            if (parkings.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static void AdminAddParking(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Nouvelle place", UIPanel.PanelType.Input, player, () => AdminAddParking(player));
            panel.TextLines.Add("Au centre de la place, regardez");
            panel.TextLines.Add("vers l'avant de la voiture. Nom :");
            panel.SetInputPlaceholder("Place A1");
            panel.PreviousButtonWithAction("Créer", async () =>
            {
                OccazParking parking = new OccazParking { Name = Limit(string.IsNullOrWhiteSpace(panel.inputText) ? "Place" : panel.inputText.Trim(), 30) };
                SetFromPlayer(parking, player);
                bool ok = await parking.Save();
                player.Notify(T, ok ? $"Place « {parking.Name} » créée, point bleu placé {Config.InfoPointDistance:0.#} m devant." : "Erreur lors de l'enregistrement.",
                    ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 6f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async void AdminParking(Player player, OccazParking parking)
        {
            OccazListing listing = (await OccazService.ActiveListings()).FirstOrDefault(l => l.ParkingId == parking.Id);

            Panel panel = Context.PanelHelper.Create($"Occaz - {parking.Name}", UIPanel.PanelType.Tab, player, () => AdminParking(player, parking));
            if (listing != null)
                panel.AddTabLine(Color($"Occupée : {listing.Model} ({listing.SellerName})", Colors.Warning), _ => Details(player, listing));
            panel.AddTabLine("Se téléporter à la place", _ => player.setup.TargetSetPosition(parking.Position + Vector3.up));
            panel.AddTabLine("Déplacer la place ici", async _ =>
            {
                SetFromPlayer(parking, player);
                await SaveParking(player, parking, "Place déplacée à votre position.");
            });
            panel.AddTabLine("Placer le point bleu d'annonce ici", async _ =>
            {
                Vector3 pos = player.setup.transform.position;
                parking.PointX = pos.x;
                parking.PointY = pos.y;
                parking.PointZ = pos.z;
                await SaveParking(player, parking, "Point bleu d'annonce placé à votre position.");
            });
            panel.AddTabLine(parking.HasCustomPoint ? "Point bleu : remettre en automatique" : Color("Point bleu : automatique (devant)", Colors.Grey), async _ =>
            {
                parking.PointX = parking.PointY = parking.PointZ = 0;
                await SaveParking(player, parking, "Point bleu remis devant la voiture.");
            });
            panel.AddTabLine("Renommer", _ => EditText(player, "Nom de la place", parking.Name, 30, async v =>
            {
                if (string.IsNullOrEmpty(v)) return;
                parking.Name = v;
                await parking.Save();
            }));
            panel.AddTabLine(Color("Supprimer la place", Colors.Error), async _ =>
            {
                if (listing != null)
                {
                    OccazService.Fail(player, "Place occupée : retirez d'abord l'annonce.");
                    return;
                }
                await parking.Delete();
                player.Notify(T, $"Place « {parking.Name} » supprimée.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });
            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async Task SaveParking(Player player, OccazParking parking, string message)
        {
            bool ok = await parking.Save();
            player.Notify(T, ok ? message : "Erreur lors de l'enregistrement.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
            if (ok) OccazInfoPoints.RefreshAll();
        }

        private static void SetFromPlayer(OccazParking parking, Player player)
        {
            Transform t = player.setup.transform;
            parking.X = t.position.x;
            parking.Y = t.position.y;
            parking.Z = t.position.z;
            parking.RotY = t.eulerAngles.y;
        }

        // ---------- Annonces et statistiques ----------

        private static async void AdminListings(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<OccazListing> listings = await OccazService.ActiveListings();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Occaz - Annonces en cours", UIPanel.PanelType.TabPrice, player, () => AdminListings(player));
            if (listings.Count == 0)
                panel.AddTabLine("Aucune annonce en cours", "", OccazService.Icon(0), _ => { });
            foreach (OccazListing listing in listings)
            {
                OccazListing l = listing;
                panel.AddTabLine($"#{l.Id} {l.Model} ({l.Plate}) - {l.SellerName}", M(l.Price), OccazService.Icon(l.ModelId), async _ =>
                {
                    switch (action)
                    {
                        case "see":
                            Details(player, l);
                            break;
                        case "tp":
                            Vector3? position = OccazService.VehiclePosition(Nova.v.GetVehicle(l.VehicleDbId));
                            if (position != null) player.setup.TargetSetPosition(position.Value + Vector3.right * 3f);
                            break;
                        case "remove":
                            if (await OccazService.Cancel(player, l, ListingStatus.Cancelled, $"retirée par le staff ({player.FullName})"))
                                player.Notify(T, "Annonce retirée.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }
            if (listings.Count > 0)
            {
                panel.AddButton("Voir", _ => { action = "see"; panel.SelectTab(); });
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton(Color("Forcer le retrait", Colors.Error), _ => { action = "remove"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static async void AdminHistory(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<OccazListing> sold = (await OccazListing.Query(l => l.Status == ListingStatus.Sold)).OrderByDescending(l => l.ClosedAt).ToList();
            int icon = OccazService.Icon(0);

            Panel panel = Context.PanelHelper.Create("Occaz - Statistiques", UIPanel.PanelType.TabPrice, player, () => AdminHistory(player));
            panel.AddTabLine(Bold("Véhicules vendus"), sold.Count.ToString(), icon, _ => { });
            panel.AddTabLine(Bold("Argent échangé"), M(sold.Sum(l => l.SoldPrice)), icon, _ => { });
            panel.AddTabLine(Bold("Commissions prélevées"), M(sold.Sum(l => l.Commission)), icon, _ => { });
            foreach (OccazListing l in sold.Take(30))
                panel.AddTabLine($"{OccazService.Date(l.ClosedAt)} {l.Model} : {l.SellerName} > {l.BuyerName}", M(l.SoldPrice),
                    OccazService.Icon(l.ModelId), _ => { });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }
    }

    // ----------------------------------------------------------------------
    //  Point bleu « Occaz » (placé par le staff via AAMenu → Points bleus)
    // ----------------------------------------------------------------------

    public class OccazPattern : ModEntity<OccazPattern>, PatternData
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore] public string TypeName { get; set; } = nameof(OccazPattern);

        [Ignore] public ModKit.ModKit Context { get; set; }

        public OccazPattern() { }

        public OccazPattern(bool isCreated)
        {
            TypeName = nameof(OccazPattern);
        }

        public void OnPlayerTrigger(Player player)
        {
            OccazMenus.Main(player, PatternName);
        }

        public async Task SetProperties(int id)
        {
            OccazPattern result = await Query(id);
            Id = id;
            TypeName = nameof(OccazPattern);
            PatternName = result?.PatternName;
        }

        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            Context ??= OccazPlugin.Instance;

            List<OccazPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Occaz - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            foreach (OccazPattern pattern in patterns)
            {
                OccazPattern p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName}", async _ =>
                {
                    p.TypeName = nameof(OccazPattern);
                    p.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, p))
                        player.Notify(OccazPlugin.Title, $"Point « {p.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(OccazPlugin.Title, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }
            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            OccazMenus.Show(panel);
        }

        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Nouveau modèle de point", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom du point (titre du menu)");
            panel.SetInputPlaceholder("Occaz By Loris Strange");
            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                OccazPattern pattern = new OccazPattern(false) { PatternName = string.IsNullOrEmpty(name) ? "Occaz By Loris Strange" : name };
                bool ok = await pattern.Save();
                player.Notify(OccazPlugin.Title, ok ? $"Modèle « {pattern.PatternName} » créé. Choisissez-le puis « Placer ici »." : "Erreur lors de l'enregistrement.",
                    ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            OccazMenus.Show(panel);
        }

        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<OccazPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Occaz - Modèles de point", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });
            foreach (OccazPattern pattern in patterns)
            {
                OccazPattern p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName}", async _ =>
                {
                    p.TypeName = nameof(OccazPattern);
                    p.Context = Context;
                    if (action == "rename")
                    {
                        Rename(player, p);
                    }
                    else if (action == "delete")
                    {
                        await Context.PointHelper.DeleteNPointsByPattern(player, p);
                        bool ok = await p.Delete();
                        player.Notify(OccazPlugin.Title, ok ? $"Modèle « {p.PatternName} » et ses points supprimés." : "Erreur lors de la suppression.",
                            ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
                        panel.Refresh();
                    }
                });
            }
            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            OccazMenus.Show(panel);
        }

        private void Rename(Player player, OccazPattern pattern)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Renommer", UIPanel.PanelType.Input, player, () => Rename(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName ?? "Occaz");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                pattern.PatternName = name;
                return await pattern.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            OccazMenus.Show(panel);
        }

        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(OccazPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Occaz - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));
            if (points.Count == 0)
                panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                NPoint pt = point;
                string name = names.TryGetValue(pt.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{pt.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, pt);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, pt))
                                player.Notify(OccazPlugin.Title, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(pt);
                            player.Notify(OccazPlugin.Title, $"Point #{pt.Id} supprimé.", NotificationManager.Type.Success, 5f);
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
            OccazMenus.Show(panel);
        }
    }
}
