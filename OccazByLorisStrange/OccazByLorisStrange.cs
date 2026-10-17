using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Life;
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
    /// Le vendeur passe dans un point bleu « Occaz », choisit sa voiture et remplit l'annonce :
    /// la voiture est déplacée sur une place de parking configurée par le staff et verrouillée.
    /// Les acheteurs la voient dans AAMenu (Interactions) quand ils sont à côté, ou dans le
    /// catalogue du point bleu, et peuvent l'acheter ou faire une offre.
    /// </summary>
    public class OccazPlugin : ModKit.ModKit
    {
        public const string Title = "Occaz";

        public static OccazPlugin Instance { get; private set; }

        public OccazConfig Config { get; private set; } = new OccazConfig();

        private string ConfigPath => Path.Combine(PluginPath, AssemblyHelper.GetName(), "config.json");

        private int _minutes;

        public OccazPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "anthoxxx (Loris Strange)");
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
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Occaz (vente de véhicules)", pattern, this);
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Occaz : véhicule en vente à proximité",
                    ui => OccazMenus.NearbyListing(PanelHelper.ReturnPlayerFromPanel(ui)));
                if (Config.ProximityObjectId > 0)
                    AAMenu.Menu.AddProximityTabLine(PluginInformations, Config.ProximityObjectId, "Occaz : véhicule en vente",
                        ui => OccazMenus.NearbyListing(PanelHelper.ReturnPlayerFromPanel(ui)));
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, Config.AdminLevel, "Occaz By Loris Strange",
                    ui => OccazMenus.AdminMain(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogError(PluginInformations.SourceName, "AAMenu introuvable : ce plugin a besoin d'AAMenu.");
            }

            Nova.server.OnMinutePassedEvent += OnMinutePassed;

            await OccazService.LoadCache();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé (Occaz By Loris Strange)");
        }

        public void LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                if (File.Exists(ConfigPath))
                    Config = JsonConvert.DeserializeObject<OccazConfig>(File.ReadAllText(ConfigPath)) ?? new OccazConfig();
                // Réécrit le fichier pour y ajouter les nouvelles options éventuelles
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, $"Config illisible ({ConfigPath}) : {ex.Message}");
                Config = new OccazConfig();
            }
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
            _ = OccazService.PayPendingPayouts(player);
        }

        public override void OnPlayerEnterVehicle(Vehicle vehicle, int seatId, Player player)
        {
            base.OnPlayerEnterVehicle(vehicle, seatId, player);
            if (vehicle == null || !OccazService.IsListed(vehicle.VehicleDbId)) return;

            // Véhicule en vente : personne ne monte dedans, pas même le vendeur
            vehicle.EjectAll();
            vehicle.NetworkisLocked = true;
            player.Notify(Title, "Ce véhicule est en vente : vous ne pouvez pas monter dedans. Annonce : AAMenu > Interactions.",
                NotificationManager.Type.Warning, 6f);
        }

        private void OnMinutePassed()
        {
            _minutes++;
            OccazService.RelockAll();
            if (_minutes % 5 == 0)
                _ = OccazService.CheckListings();
        }
    }

    /// <summary>Fichier Plugins/OccazByLorisStrange/config.json (créé au premier démarrage).</summary>
    public class OccazConfig
    {
        /// <summary>URL du webhook Discord (laisser vide pour désactiver).</summary>
        public string WebhookUrl { get; set; } = "";
        /// <summary>Nom affiché en pied des messages Discord.</summary>
        public string WebhookFooter { get; set; } = "Occaz By Loris Strange";
        /// <summary>Commission prélevée sur le prix de vente (en %), elle disparaît (taxe).</summary>
        public double CommissionPercent { get; set; } = 5;
        /// <summary>Frais fixes payés par le vendeur pour publier une annonce.</summary>
        public double ListingFee { get; set; } = 100;
        public double MinPrice { get; set; } = 500;
        public double MaxPrice { get; set; } = 5000000;
        public int MaxListingsPerPlayer { get; set; } = 2;
        /// <summary>Durée d'une annonce en heures réelles, ensuite elle expire et la voiture est rendue.</summary>
        public int ListingDurationHours { get; set; } = 72;
        /// <summary>Distance max (m) entre le vendeur (au point bleu) et la voiture qu'il veut vendre.</summary>
        public float SellerVehicleRange { get; set; } = 30f;
        /// <summary>Distance max (m) entre un joueur et une voiture en vente pour la voir dans AAMenu.</summary>
        public float ProximityRange { get; set; } = 6f;
        /// <summary>Si true, la voiture est déplacée sur une place libre. Si false, elle doit déjà être garée sur une place.</summary>
        public bool TeleportToParking { get; set; } = true;
        /// <summary>Rayon (m) d'une place de parking quand TeleportToParking = false.</summary>
        public float ParkingSpotRadius { get; set; } = 4f;
        /// <summary>Autoriser le paiement par la banque (en plus des espèces).</summary>
        public bool AllowBankPayment { get; set; } = true;
        /// <summary>Autoriser les offres (négociation) des acheteurs.</summary>
        public bool AllowOffers { get; set; } = true;
        /// <summary>Id d'objet (panneau...) qui ajoute aussi la ligne dans AAMenu → Proximité. 0 = désactivé.</summary>
        public int ProximityObjectId { get; set; } = 0;
        /// <summary>Niveau admin minimum pour la gestion dans AAMenu.</summary>
        public int AdminLevel { get; set; } = 1;
        /// <summary>Modèles de véhicules interdits à la vente (ids de modèle : police, pompiers...).</summary>
        public List<int> BlacklistedModelIds { get; set; } = new List<int>();
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

    /// <summary>Place de parking où sont exposées les voitures en vente.</summary>
    public class OccazParking : ModEntity<OccazParking>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float RotY { get; set; }

        [Ignore] public Vector3 Position => new Vector3(X, Y, Z);
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
        public int Mileage { get; set; }
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

        public static bool IsListed(int vehicleDbId) => ListedVehicles.Contains(vehicleDbId);

        public static Player FindOnline(int characterId) =>
            Nova.server.Players.FirstOrDefault(p => p != null && p.character != null && p.character.Id == characterId);

        public static async Task LoadCache()
        {
            ListedVehicles.Clear();
            foreach (OccazListing listing in await ActiveListings())
                ListedVehicles.Add(listing.VehicleDbId);
            RelockAll();
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

        /// <summary>Place libre sur laquelle la voiture est déjà garée (mode TeleportToParking = false).</summary>
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

        /// <summary>Publie l'annonce : vérifications, frais, déplacement sur le parking, verrouillage.</summary>
        public static async Task<bool> CreateListing(Player player, OccazListing draft)
        {
            if (Busy.Contains(-draft.VehicleDbId)) return false;
            Busy.Add(-draft.VehicleDbId);
            try
            {
                LifeVehicle vehicle = Nova.v.GetVehicle(draft.VehicleDbId);
                if (vehicle == null || vehicle.instance == null || OwnerOf(vehicle) != player.character.Id || vehicle.bizId != 0)
                    return Fail(player, "Ce véhicule n'est plus disponible (sorti, rangé ou vous n'en êtes plus propriétaire).");
                if (IsListed(vehicle.vehicleId))
                    return Fail(player, "Ce véhicule est déjà en vente.");
                if (Config.BlacklistedModelIds.Contains(vehicle.modelId))
                    return Fail(player, "Ce modèle ne peut pas être vendu d'occasion.");
                if (draft.Price < Config.MinPrice || draft.Price > Config.MaxPrice)
                    return Fail(player, $"Le prix doit être compris entre {Money(Config.MinPrice)} et {Money(Config.MaxPrice)}.");

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
                    return Fail(player, "Impossible de déplacer le véhicule sur le parking : il a été rangé, ressortez-le de votre garage et réessayez.");

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

                player.Notify(Title, $"Votre {draft.Model} est en vente pour {Money(draft.Price)} sur la place « {parking.Name} ».",
                    NotificationManager.Type.Success, 8f);
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
                    return Fail(buyer, "Le paiement par banque est désactivé.");
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

                listing.BuyerCharacterId = buyer.character.Id;
                listing.BuyerName = buyer.FullName;
                listing.SoldPrice = price;
                await Close(listing, ListingStatus.Sold);

                double commission = Math.Round(price * Config.CommissionPercent / 100.0);
                double net = price - commission;
                await PaySeller(listing.SellerCharacterId, net, $"Occaz : vente {listing.Plate}");

                buyer.Notify(Title, $"Félicitations ! Vous êtes le nouveau propriétaire du {listing.Model} ({listing.Plate}).",
                    NotificationManager.Type.Success, 8f);
                if (vehicle.instance != null) buyer.setup.TargetSetGPSTarget(vehicle.instance.transform.position);

                Webhook("#2196F3", "✅ Véhicule vendu", $"**{listing.Model}** a trouvé preneur.", listing,
                    ("Acheteur", buyer.FullName), ("Prix payé", Money(price)), ("Commission", Money(commission)),
                    ("Paiement", useBank ? "Banque" : "Espèces"));
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
                seller.Notify(Title, $"Votre véhicule a été vendu ! {Money(amount)} ont été versés sur votre compte bancaire.",
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
                player.Notify(Title, $"Pendant votre absence, vos ventes Occaz vous ont rapporté {Money(total)} (versés en banque).",
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
                    seller.Notify(Title, $"Votre annonce {fresh.Model} ({fresh.Plate}) a été retirée : {reason}. Venez récupérer votre véhicule au parking.",
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
                    ? $"Le vendeur a accepté votre offre de {Money(fresh.Amount)} pour le {listing.Model} ! Achetez-le au point Occaz > Mes offres."
                    : $"Le vendeur a refusé votre offre de {Money(fresh.Amount)} pour le {listing.Model}.",
                accept ? NotificationManager.Type.Success : NotificationManager.Type.Warning, 10f);
            return true;
        }

        public static async Task<bool> ChangePrice(Player seller, OccazListing listing, double price)
        {
            if (price < Config.MinPrice || price > Config.MaxPrice)
                return Fail(seller, $"Le prix doit être compris entre {Money(Config.MinPrice)} et {Money(Config.MaxPrice)}.");
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
                $"{listing.Mileage:N0} km", listing.Condition ?? "-", listing.SellerName ?? "-"
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

            Task.Run(async () =>
            {
                try
                {
                    await new DiscordWebhookClient(url).SendEmbedAsync(color, title, description, names, values, true, true, Config.WebhookFooter);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Occaz webhook", ex.Message);
                }
            });
        }
    }

    // ----------------------------------------------------------------------
    //  Menus joueurs et staff
    // ----------------------------------------------------------------------

    public static class OccazMenus
    {
        private const string T = OccazPlugin.Title;
        private static OccazPlugin Context => OccazPlugin.Instance;
        private static OccazConfig Config => OccazPlugin.Instance.Config;

        private static readonly string[] Conditions = { "Comme neuf", "Très bon état", "Bon état", "État correct", "À réparer", "Épave" };

        private static string M(double amount) => OccazService.Money(amount);

        // ---------- Point bleu ----------

        public static async void Main(Player player, string pointName)
        {
            List<OccazListing> active = await OccazService.ActiveListings();
            int mine = active.Count(l => l.SellerCharacterId == player.character.Id);
            List<OccazOffer> myOffers = await OccazOffer.Query(o => o.BuyerCharacterId == player.character.Id && o.Status == OfferStatus.Accepted);

            Panel panel = Context.PanelHelper.Create(pointName ?? "Occaz By Loris Strange", UIPanel.PanelType.Tab, player, () => Main(player, pointName));
            panel.AddTabLine("Vendre mon véhicule", _ => SellChooseVehicle(player));
            panel.AddTabLine($"Catalogue des occasions ({active.Count})", _ => Catalogue(player));
            panel.AddTabLine($"Mes annonces ({mine})", _ => MyListings(player));
            if (Config.AllowOffers)
                panel.AddTabLine(myOffers.Count > 0 ? Color($"Mes offres ({myOffers.Count} acceptée(s))", Colors.Success) : "Mes offres", _ => MyOffers(player));
            panel.AddTabLine("Comment ça marche ?", _ => Help(player));
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private static void Help(Player player)
        {
            // Panneau texte du jeu : 6 lignes courtes maximum, sinon le texte passe sous le titre
            Panel panel = Context.PanelHelper.Create("Occaz - Aide", UIPanel.PanelType.Text, player, () => Help(player));
            panel.TextLines.Add(Bold("VENDRE") + " : garez-vous ici, puis Vendre.");
            panel.TextLines.Add($"Exposée et verrouillée {Config.ListingDurationHours} h max.");
            panel.TextLines.Add($"Frais {M(Config.ListingFee)} - commission {Config.CommissionPercent}%.");
            panel.TextLines.Add("Paiement en banque, même hors ligne.");
            panel.TextLines.Add(Bold("ACHETER") + " : AAMenu > Interactions");
            panel.TextLines.Add("près du véhicule, ou le Catalogue.");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------- Vente ----------

        private static void SellChooseVehicle(Player player)
        {
            List<LifeVehicle> vehicles = OccazService.SellableVehicles(player);

            Panel panel = Context.PanelHelper.Create("Occaz - Quel véhicule vendre ?", UIPanel.PanelType.TabPrice, player, () => SellChooseVehicle(player));
            if (vehicles.Count == 0)
                panel.AddTabLine($"Aucun de vos véhicules n'est garé à moins de {Config.SellerVehicleRange:0} m", "", OccazService.Icon(0), _ => { });
            foreach (LifeVehicle vehicle in vehicles)
            {
                LifeVehicle v = vehicle;
                panel.AddTabLine(OccazService.ModelName(v.modelId), v.plate, OccazService.Icon(v.modelId), _ =>
                {
                    OccazListing draft = new OccazListing { VehicleDbId = v.vehicleId, ModelId = v.modelId, Plate = v.plate, Model = OccazService.ModelName(v.modelId) };
                    SellModel(player, draft);
                });
            }
            if (vehicles.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellModel(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Annonce (1/5) : modèle", UIPanel.PanelType.Input, player, () => SellModel(player, draft));
            panel.TextLines.Add($"Nom du modèle ({draft.Plate})");
            panel.TextLines.Add(Color("Vide = " + draft.RealModelName, Colors.Grey));
            panel.SetInputPlaceholder(draft.RealModelName);
            panel.NextButton("Suivant", () =>
            {
                string text = panel.inputText?.Trim();
                draft.Model = string.IsNullOrEmpty(text) ? draft.RealModelName : Limit(text, 40);
                SellPrice(player, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellPrice(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Annonce (2/5) : prix", UIPanel.PanelType.Input, player, () => SellPrice(player, draft));
            panel.TextLines.Add($"Prix : {M(Config.MinPrice)} à {M(Config.MaxPrice)}");
            panel.TextLines.Add(Color($"Commission : {Config.CommissionPercent}%", Colors.Warning));
            panel.SetInputPlaceholder("15000");
            panel.NextButton("Suivant", () =>
            {
                if (!TryParseMoney(panel.inputText, out double price) || price < Config.MinPrice || price > Config.MaxPrice)
                {
                    OccazService.Fail(player, $"Prix invalide (entre {M(Config.MinPrice)} et {M(Config.MaxPrice)}).");
                    panel.Refresh();
                    return;
                }
                draft.Price = price;
                SellMileage(player, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellMileage(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Annonce (3/5) : kilométrage", UIPanel.PanelType.Input, player, () => SellMileage(player, draft));
            panel.TextLines.Add("Kilométrage (en km)");
            panel.SetInputPlaceholder("45000");
            panel.NextButton("Suivant", () =>
            {
                if (!int.TryParse(panel.inputText?.Replace(" ", ""), out int km) || km < 0 || km > 2000000)
                {
                    OccazService.Fail(player, "Kilométrage invalide.");
                    panel.Refresh();
                    return;
                }
                draft.Mileage = km;
                SellCondition(player, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellCondition(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Annonce (4/5) : état", UIPanel.PanelType.Tab, player, () => SellCondition(player, draft));
            foreach (string condition in Conditions)
            {
                string c = condition;
                panel.AddTabLine(c, _ =>
                {
                    draft.Condition = c;
                    SellDescription(player, draft);
                });
            }
            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellDescription(Player player, OccazListing draft)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Annonce (5/5) : description", UIPanel.PanelType.Input, player, () => SellDescription(player, draft));
            panel.TextLines.Add("Description (facultatif)");
            panel.SetInputPlaceholder("Vidange faite, jantes neuves, première main...");
            panel.NextButton("Suivant", () =>
            {
                draft.Description = Limit(panel.inputText?.Trim() ?? "", 200);
                SellRecap(player, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellRecap(Player player, OccazListing draft)
        {
            double commission = Math.Round(draft.Price * Config.CommissionPercent / 100.0);

            Panel panel = Context.PanelHelper.Create("Occaz - Récapitulatif", UIPanel.PanelType.TabPrice, player, () => SellRecap(player, draft));
            AddListingLines(panel, draft);
            Info(panel, draft, "Frais d'annonce (espèces)", M(Config.ListingFee));
            Info(panel, draft, $"Commission {Config.CommissionPercent}%", M(commission));
            Info(panel, draft, Bold("Vous toucherez"), Color(Bold(M(draft.Price - commission)), Colors.Success));

            panel.CloseButtonWithAction("Publier l'annonce", async () => await OccazService.CreateListing(player, draft));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        // ---------- Achat ----------

        /// <summary>AAMenu → Interactions : annonce du véhicule en vente le plus proche.</summary>
        public static async void NearbyListing(Player player)
        {
            if (player == null) return;
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
                panel.AddTabLine($"{l.Model} - {l.Mileage:N0} km - {place}", M(l.Price), OccazService.Icon(l.ModelId), _ => Details(player, l));
            }
            if (listings.Count > 0) panel.NextButton("Voir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Ligne « nom | valeur » d'un panneau TabPrice (non cliquable).</summary>
        private static void Info(Panel panel, OccazListing l, string name, string value, Action<UIPanel> action = null)
        {
            panel.AddTabLine(name, value, OccazService.Icon(l.ModelId), action ?? (_ => { }));
        }

        /// <summary>Fiche de l'annonce, une information par ligne (le panneau texte est trop petit).</summary>
        private static void AddListingLines(Panel panel, OccazListing l)
        {
            Info(panel, l, Bold(l.Model), Color(Bold(M(l.Price)), Colors.Success));
            if (l.RealModelName != l.Model)
                Info(panel, l, "Modèle officiel", l.RealModelName);
            Info(panel, l, "Kilométrage", $"{l.Mileage:N0} km");
            Info(panel, l, "État", l.Condition);
            Info(panel, l, "Plaque", l.Plate);
            if (!string.IsNullOrEmpty(l.SellerName))
                Info(panel, l, "Vendeur", l.SellerName);
            if (l.ExpiresAt > 0)
                Info(panel, l, "Fin de l'annonce", OccazService.Date(l.ExpiresAt));
            if (!string.IsNullOrWhiteSpace(l.Description))
                Info(panel, l, "Description", l.Description.Length > 25 ? l.Description.Substring(0, 25) + "..." : l.Description);
        }

        private static void Description(Player player, OccazListing l)
        {
            Panel panel = Context.PanelHelper.Create($"Occaz - Description", UIPanel.PanelType.Text, player, () => Description(player, l));
            panel.TextLines.Add(Bold(l.Model));
            panel.TextLines.Add(l.Description);
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public static void Details(Player player, OccazListing listing)
        {
            bool isSeller = listing.SellerCharacterId == player.character.Id;

            Panel panel = Context.PanelHelper.Create($"Occaz - {listing.Model}", UIPanel.PanelType.TabPrice, player, () => Details(player, listing));
            AddListingLines(panel, listing);

            if (isSeller)
            {
                panel.NextButton("Gérer", () => ManageListing(player, listing));
            }
            else
            {
                panel.NextButton("Acheter", () => ConfirmBuy(player, listing, null));
                if (Config.AllowOffers) panel.NextButton("Faire une offre", () => Offer(player, listing));
            }
            if (!string.IsNullOrWhiteSpace(listing.Description))
                panel.NextButton("Description", () => Description(player, listing));
            panel.AddButton("Localiser", _ => Locate(player, listing));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
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
            panel.Display();
        }

        private static void Offer(Player player, OccazListing listing)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Faire une offre", UIPanel.PanelType.Input, player, () => Offer(player, listing));
            panel.TextLines.Add($"Prix affiché : {M(listing.Price)}");
            panel.TextLines.Add("Votre offre :");
            panel.SetInputPlaceholder(M(Math.Round(listing.Price * 0.9)));
            panel.PreviousButtonWithAction("Envoyer", async () =>
                TryParseMoney(panel.inputText, out double amount)
                    ? await OccazService.MakeOffer(player, listing, amount)
                    : OccazService.Fail(player, "Montant invalide."));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
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
                    OfferStatus.Accepted => Color("Acceptée - cliquez pour acheter", Colors.Success),
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
            panel.Display();
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
            panel.Display();
        }

        private static async void ManageListing(Player player, OccazListing listing)
        {
            List<OccazOffer> pending = await OccazOffer.Query(o => o.ListingId == listing.Id && o.Status == OfferStatus.Pending);

            Panel panel = Context.PanelHelper.Create($"Occaz - Gérer : {listing.Model}", UIPanel.PanelType.Tab, player, () => ManageListing(player, listing));
            panel.AddTabLine($"Changer le prix (actuel : {M(listing.Price)})", _ => EditPrice(player, listing));
            if (Config.AllowOffers)
                panel.AddTabLine(pending.Count > 0 ? Color($"Offres reçues ({pending.Count})", Colors.Warning) : "Offres reçues (0)", _ => Offers(player, listing));
            panel.AddTabLine("Localiser mon véhicule", _ => Locate(player, listing));
            panel.AddTabLine(Color("Retirer de la vente", Colors.Error), _ => ConfirmCancel(player, listing));
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditPrice(Player player, OccazListing listing)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Nouveau prix", UIPanel.PanelType.Input, player, () => EditPrice(player, listing));
            panel.TextLines.Add($"Prix actuel : {M(listing.Price)}");
            panel.SetInputPlaceholder(M(listing.Price));
            panel.PreviousButtonWithAction("Valider", async () =>
                TryParseMoney(panel.inputText, out double price)
                    ? await OccazService.ChangePrice(player, listing, price)
                    : OccazService.Fail(player, "Prix invalide."));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
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
            panel.Display();
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
            panel.Display();
        }

        // ---------- Staff (AAMenu → Administration → Plugins) ----------

        private static bool CheckAdmin(Player player)
        {
            if (player != null && player.IsAdmin && player.serviceAdmin) return true;
            OccazService.Fail(player, "Vous devez être staff et en service admin.");
            return false;
        }

        public static async void AdminMain(Player player)
        {
            if (!CheckAdmin(player)) return;
            int parkings = (await OccazParking.QueryAll()).Count;
            int active = (await OccazService.ActiveListings()).Count;

            Panel panel = Context.PanelHelper.Create("Occaz By Loris Strange - Staff", UIPanel.PanelType.Tab, player, () => AdminMain(player));
            panel.AddTabLine($"Places de parking ({parkings})", _ => AdminParkings(player));
            panel.AddTabLine($"Annonces en cours ({active})", _ => AdminListings(player));
            panel.AddTabLine("Historique des ventes", _ => AdminHistory(player));
            panel.AddTabLine("Points bleus Occaz", _ => new OccazPattern(false) { Context = Context }.CreateOrGenerate(player));
            panel.AddTabLine("Recharger la configuration", _ =>
            {
                Context.LoadConfig();
                player.Notify(T, "Configuration rechargée (les lignes AAMenu changent au redémarrage).", NotificationManager.Type.Success, 5f);
            });
            panel.AddTabLine("Tester le webhook Discord", _ =>
            {
                if (string.IsNullOrWhiteSpace(Config.WebhookUrl))
                {
                    OccazService.Fail(player, "Aucun WebhookUrl dans la configuration.");
                    return;
                }
                OccazService.Webhook("#607D8B", "🔧 Test du webhook", $"Test envoyé par {player.FullName}.",
                    new OccazListing { Model = "Exemple", ModelId = 0, Plate = "AA-000-AA", Price = 12345, Mileage = 50000, Condition = "Bon état", SellerName = player.FullName });
                player.Notify(T, "Message de test envoyé.", NotificationManager.Type.Success, 5f);
            });
            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void AdminParkings(Player player)
        {
            List<OccazParking> parkings = await OccazParking.QueryAll();
            HashSet<int> used = new HashSet<int>((await OccazService.ActiveListings()).Select(l => l.ParkingId));
            string action = "";

            Panel panel = Context.PanelHelper.Create("Occaz - Places de parking", UIPanel.PanelType.Tab, player, () => AdminParkings(player));
            if (parkings.Count == 0)
                panel.AddTabLine("Aucune place : placez-vous sur une place et cliquez « Ajouter ici »", _ => { });
            foreach (OccazParking parking in parkings)
            {
                OccazParking p = parking;
                panel.AddTabLine($"#{p.Id} {p.Name} - {(used.Contains(p.Id) ? Color("occupée", Colors.Warning) : Color("libre", Colors.Success))}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            player.setup.TargetSetPosition(p.Position + Vector3.up);
                            break;
                        case "move":
                            SetFromPlayer(p, player);
                            await p.Save();
                            player.Notify(T, $"Place « {p.Name} » déplacée à votre position.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                        case "delete":
                            if (used.Contains(p.Id))
                            {
                                OccazService.Fail(player, "Cette place est occupée par une annonce.");
                                return;
                            }
                            await p.Delete();
                            player.Notify(T, $"Place « {p.Name} » supprimée.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }
            panel.NextButton("Ajouter ici", () => AdminAddParking(player));
            if (parkings.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AdminAddParking(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Occaz - Nouvelle place", UIPanel.PanelType.Input, player, () => AdminAddParking(player));
            panel.TextLines.Add("Au centre de la place, face au sens");
            panel.TextLines.Add("de garage. Nom de la place :");
            panel.SetInputPlaceholder("Place A1");
            panel.PreviousButtonWithAction("Créer", async () =>
            {
                OccazParking parking = new OccazParking { Name = Limit(string.IsNullOrWhiteSpace(panel.inputText) ? "Place" : panel.inputText.Trim(), 30) };
                SetFromPlayer(parking, player);
                bool ok = await parking.Save();
                player.Notify(T, ok ? $"Place « {parking.Name} » créée." : "Erreur lors de l'enregistrement.",
                    ok ? NotificationManager.Type.Success : NotificationManager.Type.Error, 5f);
                return ok;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SetFromPlayer(OccazParking parking, Player player)
        {
            Transform t = player.setup.transform;
            parking.X = t.position.x;
            parking.Y = t.position.y;
            parking.Z = t.position.z;
            parking.RotY = t.eulerAngles.y;
        }

        private static async void AdminListings(Player player)
        {
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
            panel.Display();
        }

        private static async void AdminHistory(Player player)
        {
            List<OccazListing> sold = (await OccazListing.Query(l => l.Status == ListingStatus.Sold))
                .OrderByDescending(l => l.ClosedAt).Take(30).ToList();

            Panel panel = Context.PanelHelper.Create("Occaz - 30 dernières ventes", UIPanel.PanelType.TabPrice, player, () => AdminHistory(player));
            if (sold.Count == 0)
                panel.AddTabLine("Aucune vente pour le moment", "", OccazService.Icon(0), _ => { });
            foreach (OccazListing l in sold)
                panel.AddTabLine($"{OccazService.Date(l.ClosedAt)} {l.Model} ({l.Plate}) : {l.SellerName} > {l.BuyerName}", M(l.SoldPrice),
                    OccazService.Icon(l.ModelId), _ => { });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------- Utilitaires ----------

        private static bool TryParseMoney(string text, out double amount)
        {
            string clean = (text ?? "").Replace(" ", "").Replace("€", "").Replace(" ", "").Replace(" ", "").Replace(",", ".");
            return double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount)
                   && amount > 0 && !double.IsInfinity(amount);
        }

        private static string Limit(string text, int max) => text.Length <= max ? text : text.Substring(0, max);
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
            panel.Display();
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
            panel.Display();
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
            panel.Display();
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
            panel.Display();
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
            panel.Display();
        }
    }
}
