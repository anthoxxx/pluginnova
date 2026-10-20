using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>Côté staff (AAMenu > Administration > Plugins > Uber Eats) : réglages, modération, litiges.</summary>
    public partial class UberEatsPlugin
    {
        private async void AdminMenu(Player player)
        {
            if (player == null || !player.IsAdmin) return;

            int activeOrders = (await UEOrder.QueryAll()).Count(o => o.IsActive);
            int restaurants = (await UERestaurant.QueryAll()).Count;

            Panel panel = PanelHelper.Create($"{AppName} - Administration", UIPanel.PanelType.Tab, player, () => AdminMenu(player));
            panel.AddTabLine("Réglages de la plateforme", _ => AdminSettings(player));
            panel.AddTabLine("Métier restaurateur", _ => AdminJob(player));
            panel.AddTabLine("Points de farm", _ => new FarmPattern(false) { Context = this }.CreateOrGenerate(player));
            panel.AddTabLine($"Restaurants ({restaurants})", _ => AdminRestaurants(player));
            panel.AddTabLine($"Commandes en cours ({activeOrders})", _ => AdminOrders(player));
            panel.AddTabLine($"Livreurs en service ({_onDuty.Count(id => FindPlayer(id) != null)})", _ => AdminCouriers(player));
            panel.AddTabLine("Classement des livreurs", _ => Leaderboard(player));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Réglages
        // ------------------------------------------------------------------

        private void AdminSettings(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Réglages", UIPanel.PanelType.Tab, player, () => AdminSettings(player));

            void Line(string label, string value, Action<double> apply)
            {
                panel.AddTabLine($"{label} : {Color(value, Colors.Info)}", _ => AdminEditValue(player, label, value, apply));
            }

            Line("Commission plateforme (%)", Config.CommissionPercent.ToString(CultureInfo.InvariantCulture), v => Config.CommissionPercent = Mathf.Clamp((float)v, 0, 100));
            Line("Frais de livraison de base", FormatMoney(Config.BaseDeliveryFee), v => Config.BaseDeliveryFee = v);
            Line("Frais par 100 m", FormatMoney(Config.FeePer100m), v => Config.FeePer100m = v);
            Line("Frais de livraison max", FormatMoney(Config.MaxDeliveryFee), v => Config.MaxDeliveryFee = v);
            Line("Commande minimum", FormatMoney(Config.MinOrderAmount), v => Config.MinOrderAmount = v);
            Line("Prix d'inscription restaurant", FormatMoney(Config.RestaurantCreationCost), v => Config.RestaurantCreationCost = v);
            Line("Commandes simultanées par client", Config.MaxActiveOrdersPerClient.ToString(), v => Config.MaxActiveOrdersPerClient = Math.Max(1, (int)v));
            Line("Distance de remise au client (m)", Config.HandoverDistance.ToString(CultureInfo.InvariantCulture), v => Config.HandoverDistance = Mathf.Max(1, (float)v));
            Line("Distance de retrait au restaurant (m)", Config.PickupDistance.ToString(CultureInfo.InvariantCulture), v => Config.PickupDistance = Mathf.Max(1, (float)v));
            Line("Délai de réponse du restaurant (min)", Config.PendingTimeoutMinutes.ToString(), v => Config.PendingTimeoutMinutes = Math.Max(1, (int)v));
            Line("Délai max sans livreur (min)", Config.NoCourierTimeoutMinutes.ToString(), v => Config.NoCourierTimeoutMinutes = Math.Max(1, (int)v));

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminEditValue(Player player, string label, string current, Action<double> apply)
        {
            Panel panel = PanelHelper.Create(label, UIPanel.PanelType.Input, player, () => AdminEditValue(player, label, current, apply));
            panel.TextLines.Add($"{label} (actuel : {current})");
            panel.SetInputPlaceholder("Nouvelle valeur");
            panel.PreviousButtonWithAction("Valider", () =>
            {
                if (!TryParseAmount(panel.inputText, out double value))
                {
                    player.Notify(AppName, "Valeur invalide.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }
                apply(value);
                Config.Save();
                player.Notify(AppName, "Réglage enregistré.", NotificationManager.Type.Success, 4f);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Métier restaurateur
        // ------------------------------------------------------------------

        private void AdminJob(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Métier restaurateur", UIPanel.PanelType.Tab, player, () => AdminJob(player));
            panel.AddTabLine("Réservé au métier : " + (Config.RestaurantRequiresJob ? Color("oui", Colors.Success) : Color("non (tout le monde)", Colors.Warning)), _ =>
            {
                Config.RestaurantRequiresJob = !Config.RestaurantRequiresJob;
                Config.Save();
                panel.Refresh();
            });
            panel.AddTabLine($"Activités autorisées : {Color(Config.AllowedActivities.Count.ToString(), Colors.Info)}", _ => AdminJobActivities(player));
            panel.AddTabLine($"Entreprises autorisées : {Color(Config.AllowedBizIds.Count.ToString(), Colors.Info)}", _ => AdminJobBizs(player));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Toute entreprise ayant une de ces activités accède à l'Espace restaurateur.</summary>
        private void AdminJobActivities(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Activités autorisées", UIPanel.PanelType.Tab, player, () => AdminJobActivities(player));
            foreach (Activity.Type type in Enum.GetValues(typeof(Activity.Type)))
            {
                if (type == Activity.Type.None) continue;
                string name = type.ToString();
                bool allowed = Config.AllowedActivities.Contains(name);
                panel.AddTabLine($"{name} : " + (allowed ? Color("autorisée", Colors.Success) : Color("non", Colors.Grey)), _ =>
                {
                    if (allowed) Config.AllowedActivities.Remove(name);
                    else Config.AllowedActivities.Add(name);
                    Config.Save();
                    panel.Refresh();
                });
            }
            panel.AddButton("Basculer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Autorise une entreprise précise, quelle que soit son activité.</summary>
        private void AdminJobBizs(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Entreprises autorisées", UIPanel.PanelType.Tab, player, () => AdminJobBizs(player));
            List<Bizs> bizs = Nova.biz.bizs.Where(b => b != null).OrderBy(b => b.BizName).ToList();
            if (bizs.Count == 0)
                panel.AddTabLine(Color("Aucune entreprise", Colors.Grey), _ => { });

            foreach (Bizs biz in bizs)
            {
                int bizId = biz.Id;
                bool allowed = Config.AllowedBizIds.Contains(bizId);
                panel.AddTabLine($"[{bizId}] {biz.BizName} : " + (allowed ? Color("autorisée", Colors.Success) : Color("non", Colors.Grey)), _ =>
                {
                    if (allowed) Config.AllowedBizIds.Remove(bizId);
                    else Config.AllowedBizIds.Add(bizId);
                    Config.Save();
                    panel.Refresh();
                });
            }
            if (bizs.Count > 0) panel.AddButton("Basculer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Restaurants
        // ------------------------------------------------------------------

        private async void AdminRestaurants(Player player)
        {
            List<UERestaurant> restaurants = (await UERestaurant.QueryAll()).OrderBy(r => r.Name).ToList();

            Panel panel = PanelHelper.Create($"{AppName} - Restaurants", UIPanel.PanelType.Tab, player, () => AdminRestaurants(player));
            if (restaurants.Count == 0)
                panel.AddTabLine(Color("Aucun restaurant", Colors.Grey), _ => { });

            foreach (UERestaurant restaurant in restaurants)
            {
                string state = restaurant.IsSuspended ? Color("suspendu", Colors.Error) : restaurant.IsOpen ? Color("ouvert", Colors.Success) : Color("fermé", Colors.Grey);
                panel.AddTabLine($"[{restaurant.Id}] {restaurant.Name} - {restaurant.OwnerName} - {state}", _ => AdminRestaurant(player, restaurant.Id));
            }

            if (restaurants.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminRestaurant(Player player, int restaurantId)
        {
            UERestaurant restaurant = await UERestaurant.Query(restaurantId);
            if (restaurant == null) return;

            Panel panel = PanelHelper.Create($"Restaurant [{restaurant.Id}]", UIPanel.PanelType.Tab, player, () => AdminRestaurant(player, restaurantId));
            panel.AddInfo($"{Bold(restaurant.Name)} ({restaurant.Category})");
            panel.AddInfo($"Créé par : {restaurant.OwnerName}");
            if (restaurant.BizId != 0)
                panel.AddInfo($"Entreprise : {Nova.biz.FetchBiz(restaurant.BizId)?.BizName ?? $"#{restaurant.BizId} (introuvable)"}");
            panel.AddInfo($"Commandes livrées : {restaurant.OrdersCount}");
            panel.AddInfo($"CA net : {FormatMoney(restaurant.Revenue)}");
            panel.AddInfo($"Note : {FormatRating(restaurant.Rating, restaurant.RatingCount)}");
            panel.AddInfo($"Point de retrait à {Mathf.RoundToInt(Vector3.Distance(PositionOf(player), restaurant.Position))} m");

            panel.AddAction(restaurant.IsSuspended ? Color("Lever la suspension", Colors.Success) : Color("Suspendre", Colors.Warning), async _ =>
            {
                restaurant.IsSuspended = !restaurant.IsSuspended;
                if (restaurant.IsSuspended) restaurant.IsOpen = false;
                await restaurant.Save();
                NotifyRestaurant(restaurant, restaurant.IsSuspended
                    ? "Votre restaurant a été suspendu par le staff."
                    : "La suspension de votre restaurant a été levée.", NotificationManager.Type.Warning, 8f);
                panel.Refresh();
            });
            panel.AddAction(Color("Supprimer le restaurant", Colors.Error), async _ =>
            {
                if (!await DeleteRestaurant(restaurantId, null))
                {
                    player.Notify(AppName, "Impossible : annulez d'abord ses commandes en cours.", NotificationManager.Type.Error, 6f);
                    panel.Refresh();
                    return;
                }
                player.Notify(AppName, $"Restaurant « {restaurant.Name} » supprimé.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Commandes et livreurs
        // ------------------------------------------------------------------

        private async void AdminOrders(Player player)
        {
            List<UEOrder> orders = (await UEOrder.QueryAll()).Where(o => o.IsActive).OrderBy(o => o.CreatedAt).ToList();

            Panel panel = PanelHelper.Create($"{AppName} - Commandes en cours", UIPanel.PanelType.Tab, player, () => AdminOrders(player));
            if (orders.Count == 0)
                panel.AddTabLine(Color("Aucune commande en cours", Colors.Grey), _ => { });

            foreach (UEOrder order in orders)
            {
                panel.AddTabLine($"#{order.Id} {order.RestaurantName} > {order.ClientName} - {StatusLabel(order.State)}",
                    _ => AdminOrder(player, order.Id));
            }

            if (orders.Count > 0) panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminOrder(Player player, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null) return;

            Panel panel = PanelHelper.Create($"Litige commande #{order.Id}", UIPanel.PanelType.Tab, player, () => AdminOrder(player, orderId));
            panel.AddInfo($"{order.RestaurantName} > {order.ClientName}");
            panel.AddInfo($"État : {StatusLabel(order.State)} ({FormatAgo(order.CreatedAt)})");
            panel.AddOrderLines(player, order);
            panel.AddInfo($"Total payé : {FormatMoney(order.Total)}");
            panel.AddInfo($"Livreur : {(order.CourierCharacterId != 0 ? order.CourierName : "aucun")}");

            if (order.IsCancellable)
            {
                panel.PreviousButtonWithAction(Color("Rembourser", Colors.Error), () =>
                    CancelOrder(order, OrderStatus.Cancelled, "annulée par le staff"));
            }
            else if (order.State == OrderStatus.Delivering)
            {
                // Sac déjà retiré (restaurateur payé) : on rembourse le client, la course est perdue pour le livreur.
                panel.PreviousButtonWithAction(Color("Rembourser", Colors.Error), async () =>
                {
                    UEOrder fresh = await UEOrder.Query(orderId);
                    if (fresh == null || fresh.State != OrderStatus.Delivering) return false;
                    fresh.State = OrderStatus.Cancelled;
                    fresh.UpdatedAt = Now();
                    if (!await fresh.Save()) return false;

                    await Pay(fresh.ClientCharacterId, fresh.Total, $"Remboursement commande #{fresh.Id} (staff)");
                    Player courier = FindPlayer(fresh.CourierCharacterId);
                    if (courier != null)
                    {
                        ClearGps(courier);
                        courier.Notify(AppName, $"La course #{fresh.Id} a été clôturée par le staff.", NotificationManager.Type.Warning, 8f);
                    }
                    return true;
                });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminCouriers(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Livreurs en service", UIPanel.PanelType.Tab, player, () => AdminCouriers(player));
            List<Player> couriers = _onDuty.Select(FindPlayer).Where(p => p != null).ToList();
            if (couriers.Count == 0)
                panel.AddTabLine(Color("Aucun livreur en service", Colors.Grey), _ => { });

            foreach (Player courier in couriers)
            {
                UEOrder job = await GetCurrentJob(courier.character.Id);
                string state = job != null ? Color($"course #{job.Id}", Colors.Warning) : Color("disponible", Colors.Success);
                panel.AddTabLine($"{courier.FullName} - {state}", _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
