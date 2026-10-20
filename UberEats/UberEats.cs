using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.CheckpointSystem;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.Utils;
using UnityEngine;
using Logger = ModKit.Internal.Logger;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>
    /// Uber Eats : plateforme de livraison de repas entre joueurs, entièrement dans AAMenu
    /// (Interactions > Uber Eats). Ce n'est pas un métier : n'importe qui peut être client,
    /// livreur indépendant ou ouvrir son restaurant, en plus de son emploi.
    /// </summary>
    public partial class UberEatsPlugin : ModKit.ModKit
    {
        public const string AppName = "Uber Eats";
        public const string BrandColor = "#06C167";

        public UberEatsConfig Config { get; private set; }

        /// <summary>Paniers en cours (perdus au redémarrage, rien n'est encore payé).</summary>
        private readonly Dictionary<int, Cart> _carts = new Dictionary<int, Cart>();

        /// <summary>Livreurs en service (characterId).</summary>
        private readonly HashSet<int> _onDuty = new HashSet<int>();

        /// <summary>GPS de course de chaque livreur (point bleu à pied + point orange en véhicule).</summary>
        private readonly Dictionary<int, (NCheckpoint walk, NVehicleCheckpoint drive)> _gps =
            new Dictionary<int, (NCheckpoint, NVehicleCheckpoint)>();

        public UberEatsPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            UberEatsConfig.FilePath = Path.Combine(PluginPath, "UberEats", "config.json");
            Config = UberEatsConfig.Load();

            Orm.RegisterTable<UERestaurant>();
            Orm.RegisterTable<UEMenuItem>();
            Orm.RegisterTable<UEOrder>();
            Orm.RegisterTable<UECourier>();
            Orm.RegisterTable<UEPendingPayment>();

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, Color(AppName, BrandColor), ui =>
                    MainMenu(PanelHelper.ReturnPlayerFromPanel(ui)));
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, AppName, ui =>
                    AdminMenu(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : le plugin Uber Eats a besoin d'AAMenu.");
            }

            Nova.server.OnMinutePassedEvent += OnMinutePassed;

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        public override async void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            await PayPendingPayments(player);
        }

        // ------------------------------------------------------------------
        //  Argent
        // ------------------------------------------------------------------

        /// <summary>Verse de l'argent sur le compte bancaire d'un personnage, même s'il est hors ligne.</summary>
        public async Task Pay(int characterId, double amount, string reason)
        {
            amount = Math.Round(amount, 2);
            if (amount <= 0) return;

            Player player = FindPlayer(characterId);
            if (player != null)
            {
                player.AddBankMoney(amount, reason);
                player.Notify(AppName, $"{Color("+" + FormatMoney(amount), Colors.Success)} sur votre compte : {reason}", NotificationManager.Type.Success, 6f);
                return;
            }

            await new UEPendingPayment { CharacterId = characterId, Amount = amount, Reason = reason }.Save();
        }

        private async Task PayPendingPayments(Player player)
        {
            int characterId = player.character.Id;
            List<UEPendingPayment> payments = await UEPendingPayment.Query(p => p.CharacterId == characterId);
            foreach (UEPendingPayment payment in payments)
            {
                if (!await payment.Delete()) continue;
                player.AddBankMoney(payment.Amount, payment.Reason);
                player.Notify(AppName, $"Pendant votre absence : {Color("+" + FormatMoney(payment.Amount), Colors.Success)} ({payment.Reason})", NotificationManager.Type.Success, 8f);
            }
        }

        /// <summary>Prélève sur le compte bancaire (paiement par carte dans l'application).</summary>
        private static bool Charge(Player player, double amount, string reason)
        {
            amount = Math.Round(amount, 2);
            if (player.character.Bank < amount)
            {
                player.Notify(AppName, $"Paiement refusé : solde bancaire insuffisant ({FormatMoney(amount)} requis).", NotificationManager.Type.Error, 6f);
                return false;
            }
            player.AddBankMoney(-amount, reason);
            return true;
        }

        public double ComputeDeliveryFee(Vector3 from, Vector3 to)
        {
            double fee = Config.BaseDeliveryFee + Config.FeePer100m * (Vector3.Distance(from, to) / 100f);
            return Math.Round(Math.Min(fee, Config.MaxDeliveryFee), 2);
        }

        // ------------------------------------------------------------------
        //  Cycle de vie des commandes
        // ------------------------------------------------------------------

        /// <summary>Annule (ou refuse) une commande : remise en stock, remboursement intégral, notifications.</summary>
        public async Task<bool> CancelOrder(UEOrder order, OrderStatus newState, string reason)
        {
            order = await UEOrder.Query(order.Id);
            if (order == null || !order.IsCancellable) return false;

            order.State = newState;
            order.UpdatedAt = Now();
            if (!await order.Save()) return false;

            foreach (OrderLine line in order.Lines)
            {
                UEMenuItem menuItem = await UEMenuItem.Query(line.MenuItemId);
                if (menuItem == null) continue;
                menuItem.Stock += line.Quantity;
                await menuItem.Save();
            }

            await Pay(order.ClientCharacterId, order.Total, $"Remboursement commande #{order.Id}");
            FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"Commande #{order.Id} {(newState == OrderStatus.Refused ? "refusée" : "annulée")} : {reason}", NotificationManager.Type.Warning, 8f);

            if (order.CourierCharacterId != 0)
            {
                Player courier = FindPlayer(order.CourierCharacterId);
                if (courier != null)
                {
                    ClearGps(courier);
                    courier.Notify(AppName, $"La course #{order.Id} a été annulée : {reason}", NotificationManager.Type.Warning, 8f);
                }
            }

            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant != null)
                FindPlayer(restaurant.OwnerCharacterId)?.Notify(AppName, $"Commande #{order.Id} annulée : {reason}", NotificationManager.Type.Warning, 6f);

            return true;
        }

        /// <summary>Le livreur récupère le sac au restaurant : le restaurateur est payé et la course part vers le client.</summary>
        public async Task<bool> TryPickup(Player courier, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.CourierCharacterId != courier.character.Id || order.State == OrderStatus.Delivering)
                return false;

            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant == null) return false;

            if (Vector3.Distance(PositionOf(courier), restaurant.Position) > Config.PickupDistance)
            {
                courier.Notify(AppName, "Vous devez être au point de retrait du restaurant.", NotificationManager.Type.Warning, 5f);
                return false;
            }
            if (order.State != OrderStatus.Ready)
            {
                courier.Notify(AppName, "La commande est encore en cuisine, patientez quelques instants.", NotificationManager.Type.Info, 5f);
                FindPlayer(restaurant.OwnerCharacterId)?.Notify(AppName, $"Le livreur {courier.FullName} attend la commande #{order.Id} !", NotificationManager.Type.Warning, 8f);
                return false;
            }

            order.State = OrderStatus.Delivering;
            order.UpdatedAt = Now();
            if (!await order.Save()) return false;

            double payout = Math.Round(order.Subtotal * (1 - Config.CommissionPercent / 100.0), 2);
            restaurant.OrdersCount++;
            restaurant.Revenue += payout;
            await restaurant.Save();
            await Pay(restaurant.OwnerCharacterId, payout, $"Commande #{order.Id} ({restaurant.Name})");

            courier.Notify(AppName, $"Sac récupéré ! Direction le client {order.ClientName}.", NotificationManager.Type.Success, 6f);
            FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"{order.CourierName} a récupéré votre commande, il arrive !", NotificationManager.Type.Info, 8f);

            SetGps(courier, order.Destination, async p => await OnArrivedAtClient(p, order.Id));
            return true;
        }

        private async Task OnArrivedAtClient(Player courier, int orderId)
        {
            if (await TryHandover(courier, orderId)) return;

            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.State != OrderStatus.Delivering) return;
            FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"Votre livreur {courier.FullName} est arrivé à l'adresse de livraison !", NotificationManager.Type.Warning, 10f);
        }

        /// <summary>Remise du sac en main propre : les plats passent dans l'inventaire du client et le livreur est payé.</summary>
        public async Task<bool> TryHandover(Player courier, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.CourierCharacterId != courier.character.Id || order.State != OrderStatus.Delivering)
                return false;

            Player client = FindPlayer(order.ClientCharacterId);
            if (client == null)
            {
                courier.Notify(AppName, "Le client n'est pas en ville. Réessayez plus tard ou contactez le staff.", NotificationManager.Type.Warning, 6f);
                return false;
            }
            if (Vector3.Distance(PositionOf(courier), PositionOf(client)) > Config.HandoverDistance)
            {
                courier.Notify(AppName, $"{client.FullName} n'est pas à côté de vous. Rejoignez-le pour lui remettre le sac.", NotificationManager.Type.Warning, 6f);
                return false;
            }

            // On remet ligne par ligne : si l'inventaire du client déborde, le reste attend dans le sac.
            List<OrderLine> remaining = new List<OrderLine>();
            foreach (OrderLine line in order.Lines)
            {
                if (!InventoryUtils.AddItem(client, line.ItemId, line.Quantity))
                    remaining.Add(line);
            }
            if (remaining.Count > 0)
            {
                order.Lines = remaining;
                await order.Save();
                client.Notify(AppName, "Votre inventaire est plein ! Faites de la place pour récupérer le reste de la commande.", NotificationManager.Type.Error, 8f);
                courier.Notify(AppName, "L'inventaire du client est plein : une partie de la commande est encore dans le sac.", NotificationManager.Type.Warning, 8f);
                return false;
            }

            order.State = OrderStatus.Delivered;
            order.DeliveredAt = Now();
            order.UpdatedAt = order.DeliveredAt;
            if (!await order.Save()) return false;

            ClearGps(courier);

            UECourier profile = await GetOrCreateCourier(courier);
            double bonus = CourierLevel(profile.Deliveries).bonus;
            double earned = Math.Round(order.DeliveryFee * (1 + bonus / 100.0), 2);
            profile.Deliveries++;
            profile.Earnings += earned;
            profile.Name = courier.FullName;
            await profile.Save();
            await Pay(courier.character.Id, earned, $"Course #{order.Id}" + (bonus > 0 ? $" (bonus {CourierLevel(profile.Deliveries - 1).name} +{bonus}%)" : ""));

            client.Notify(AppName, $"Bon appétit ! Pensez à noter votre commande dans « Mes commandes ».", NotificationManager.Type.Success, 8f);
            courier.Notify(AppName, $"Course #{order.Id} terminée. Merci !", NotificationManager.Type.Success, 6f);
            return true;
        }

        /// <summary>Annule automatiquement les commandes oubliées pour ne jamais bloquer l'argent d'un client.</summary>
        private async void OnMinutePassed()
        {
            try
            {
                long now = Now();
                int ready = (int)OrderStatus.Ready;
                List<UEOrder> stale = await UEOrder.Query(o => o.Status <= ready && o.CourierCharacterId == 0);
                foreach (UEOrder order in stale)
                {
                    if (order.State == OrderStatus.Pending && now - order.CreatedAt > Config.PendingTimeoutMinutes * 60)
                        await CancelOrder(order, OrderStatus.Cancelled, "le restaurant n'a pas répondu à temps");
                    else if (order.State != OrderStatus.Pending && now - order.UpdatedAt > Config.NoCourierTimeoutMinutes * 60)
                        await CancelOrder(order, OrderStatus.Cancelled, "aucun livreur disponible");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "OnMinutePassed : " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Livreurs
        // ------------------------------------------------------------------

        public static readonly (int deliveries, string name, double bonus)[] CourierLevels =
        {
            (0, "Débutant", 0),
            (10, "Confirmé", 5),
            (30, "Expert", 10),
            (75, "Pro", 15),
            (150, "Légende", 25)
        };

        public static (string name, double bonus, int next) CourierLevel(int deliveries)
        {
            int index = CourierLevels.Length - 1;
            while (index > 0 && deliveries < CourierLevels[index].deliveries) index--;
            int next = index + 1 < CourierLevels.Length ? CourierLevels[index + 1].deliveries : -1;
            return (CourierLevels[index].name, CourierLevels[index].bonus, next);
        }

        public async Task<UECourier> GetOrCreateCourier(Player player)
        {
            int characterId = player.character.Id;
            UECourier courier = (await UECourier.Query(c => c.CharacterId == characterId)).FirstOrDefault();
            if (courier != null) return courier;

            courier = new UECourier { CharacterId = characterId, Name = player.FullName };
            await courier.Save(); // InsertAsync renseigne l'Id
            return courier;
        }

        /// <summary>Prévient les livreurs en service qu'une course vient d'apparaître.</summary>
        public void AlertCouriers(UEOrder order, UERestaurant restaurant)
        {
            foreach (int characterId in _onDuty.ToList())
            {
                Player courier = FindPlayer(characterId);
                if (courier == null) { _onDuty.Remove(characterId); continue; }
                if (characterId == order.ClientCharacterId) continue;

                int distance = Mathf.RoundToInt(Vector3.Distance(PositionOf(courier), restaurant.Position));
                courier.Notify(AppName, $"Nouvelle course : {restaurant.Name} ({distance} m) → {Color(FormatMoney(order.DeliveryFee), Colors.Success)}", NotificationManager.Type.Info, 8f);
            }
        }

        public void SetGps(Player courier, Vector3 position, Action<Player> onEnter)
        {
            ClearGps(courier);

            bool triggered = false;
            void Enter()
            {
                if (triggered) return;
                triggered = true;
                ClearGps(courier);
                onEnter(courier);
            }

            NCheckpoint walk = new NCheckpoint(courier.netId, position, _ => Enter());
            NVehicleCheckpoint drive = new NVehicleCheckpoint(courier.netId, position,
                (Action<NVehicleCheckpoint, uint>)((_, __) => Enter()));
            courier.CreateCheckpoint(walk);
            courier.CreateVehicleCheckpoint(drive);
            _gps[courier.character.Id] = (walk, drive);
        }

        public void ClearGps(Player courier)
        {
            if (!_gps.TryGetValue(courier.character.Id, out var gps)) return;
            _gps.Remove(courier.character.Id);
            try
            {
                courier.DestroyCheckpoint(gps.walk);
                courier.DestroyVehicleCheckpoint(gps.drive);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(PluginInformations.SourceName, "ClearGps : " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        public static Player FindPlayer(int characterId)
        {
            return Nova.server.Players.FirstOrDefault(p => p != null && p.isSpawned && p.character != null && p.character.Id == characterId);
        }

        /// <summary>Icône par défaut des lignes TabPrice sans item (même repli que ModKit).</summary>
        public static int DefaultIcon => ItemUtils.GetIconIdByItemId(1112);

        public static Vector3 PositionOf(Player player) => player.setup.transform.position;

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string FormatMoney(double amount) => amount.ToString("0.00", CultureInfo.InvariantCulture) + " €";

        public static string FormatRating(double rating, int count)
        {
            return count == 0 ? Color("Nouveau", Colors.Info) : $"{Color(rating.ToString("0.0", CultureInfo.InvariantCulture) + "/5", Colors.Warning)} ({count} avis)";
        }

        public static string FormatAgo(long timestamp)
        {
            long minutes = Math.Max(0, (Now() - timestamp) / 60);
            if (minutes < 1) return "à l'instant";
            if (minutes < 60) return $"il y a {minutes} min";
            if (minutes < 60 * 24) return $"il y a {minutes / 60} h";
            return $"il y a {minutes / (60 * 24)} j";
        }

        public static string StatusLabel(OrderStatus status)
        {
            switch (status)
            {
                case OrderStatus.Pending: return Color("En attente du restaurant", Colors.Grey);
                case OrderStatus.Preparing: return Color("En cuisine", Colors.Orange);
                case OrderStatus.Ready: return Color("Prête", Colors.Info);
                case OrderStatus.Delivering: return Color("En livraison", Colors.Warning);
                case OrderStatus.Delivered: return Color("Livrée", Colors.Success);
                case OrderStatus.Refused: return Color("Refusée", Colors.Error);
                default: return Color("Annulée", Colors.Error);
            }
        }

        public static string ItemName(Player player, int itemId)
        {
            Item item = ItemUtils.GetItemById(itemId);
            return item != null ? player.NewTranslate("Items", item.itemName) : $"Objet #{itemId}";
        }

        public static bool TryParseAmount(string input, out double amount)
        {
            return double.TryParse(input?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
                   && amount >= 0 && !double.IsInfinity(amount);
        }

        public static bool TryParseQuantity(string input, out int quantity)
        {
            return int.TryParse(input?.Trim(), out quantity) && quantity > 0;
        }

        public static string OrderSummary(Player viewer, UEOrder order)
        {
            return string.Join(", ", order.Lines.Select(l => $"{l.Quantity}x {ItemName(viewer, l.ItemId)}"));
        }
    }

    /// <summary>Panier d'un client : un seul restaurant à la fois.</summary>
    public class Cart
    {
        public int RestaurantId;
        public readonly Dictionary<int, int> Quantities = new Dictionary<int, int>();

        public int Count => Quantities.Values.Sum();
    }
}
