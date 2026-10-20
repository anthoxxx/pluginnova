using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Utils;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>Côté client : parcourir les restaurants, remplir son panier, commander, suivre et noter.</summary>
    public partial class UberEatsPlugin
    {
        public async void MainMenu(Player player)
        {
            if (player == null) return;
            int characterId = player.character.Id;
            int active = (int)OrderStatus.Delivering;
            int activeOrders = (await UEOrder.Query(o => o.ClientCharacterId == characterId && o.Status <= active)).Count;
            int cartCount = _carts.TryGetValue(characterId, out Cart cart) ? cart.Count : 0;

            Panel panel = PanelHelper.Create(Color(AppName, BrandColor), UIPanel.PanelType.Tab, player, () => MainMenu(player));
            panel.AddTabLine("Commander un repas", _ => RestaurantsList(player));
            panel.AddTabLine("Mon panier" + (cartCount > 0 ? Color($" ({cartCount} article(s))", Colors.Info) : ""), _ => CartMenu(player));
            panel.AddTabLine("Mes commandes" + (activeOrders > 0 ? Color($" ({activeOrders} en cours)", Colors.Warning) : ""), _ => ClientOrders(player));
            panel.AddTabLine("Espace livreur" + (_onDuty.Contains(characterId) ? Color(" (en service)", Colors.Success) : ""), _ => CourierMenu(player));
            if (IsRestaurateur(player))
                panel.AddTabLine("Espace restaurateur", _ => RestaurantSpace(player));
            else
                panel.AddTabLine(Color("Espace restaurateur (réservé au métier)", Colors.Grey), _ =>
                {
                    player.Notify(AppName, "Réservé aux employés d'un restaurant partenaire.", NotificationManager.Type.Warning, 5f);
                    MainMenu(player);
                });
            panel.AddTabLine("Classement des livreurs", _ => Leaderboard(player));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Restaurants et carte
        // ------------------------------------------------------------------

        private async void RestaurantsList(Player player)
        {
            List<UERestaurant> all = await UERestaurant.Query(r => !r.IsSuspended);
            Vector3 here = PositionOf(player);
            List<UERestaurant> open = all.Where(r => r.IsOpen).OrderBy(r => Vector3.Distance(here, r.Position)).ToList();

            Panel panel = PanelHelper.Create("Restaurants ouverts", UIPanel.PanelType.Tab, player, () => RestaurantsList(player));
            if (open.Count == 0)
                panel.AddTabLine(Color("Aucun restaurant ouvert pour le moment", Colors.Grey), _ => { });

            foreach (UERestaurant restaurant in open)
            {
                int distance = Mathf.RoundToInt(Vector3.Distance(here, restaurant.Position));
                panel.AddTabLine($"{Bold(restaurant.Name)} - {Color(restaurant.Category, Colors.Grey)} - {distance} m",
                    _ => RestaurantCard(player, restaurant.Id));
            }

            int closed = all.Count - open.Count;
            if (closed > 0)
                panel.AddTabLine(Color($"{closed} restaurant(s) fermé(s)", Colors.Grey), _ => { });

            if (open.Count > 0) panel.NextButton("Voir la carte", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void RestaurantCard(Player player, int restaurantId)
        {
            UERestaurant restaurant = await UERestaurant.Query(restaurantId);
            if (restaurant == null || !restaurant.IsOpen || restaurant.IsSuspended)
            {
                player.Notify(AppName, "Ce restaurant n'est plus disponible.", NotificationManager.Type.Warning, 5f);
                return;
            }

            List<UEMenuItem> items = (await UEMenuItem.Query(m => m.RestaurantId == restaurantId))
                .Where(m => m.IsAvailable && m.Stock > 0).OrderBy(m => m.Price).ToList();

            Panel panel = PanelHelper.Create(restaurant.Name, UIPanel.PanelType.TabPrice, player, () => RestaurantCard(player, restaurantId));
            string stars = restaurant.RatingCount == 0 ? "Nouveau" : restaurant.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/5";
            panel.AddTabLine(Bold("» Infos du restaurant"), stars, DefaultIcon, _ => RestaurantInfo(player, restaurant));
            if (items.Count == 0)
                panel.AddTabLine("Plus rien en stock", "", DefaultIcon, _ => panel.Refresh());

            foreach (UEMenuItem item in items)
            {
                string name = ItemName(player, item.ItemId);
                string stock = item.Stock <= 3 ? Color($" ({item.Stock} restant)", Colors.Warning) : "";
                panel.AddTabLine(name + stock, FormatMoney(item.Price), ItemUtils.GetIconIdByItemId(item.ItemId),
                    _ => AskAddToCart(player, restaurant, item));
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.NextButton("Panier", () => CartMenu(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RestaurantInfo(Player player, UERestaurant restaurant)
        {
            Panel panel = PanelHelper.Create($"{restaurant.Name} - Infos", UIPanel.PanelType.Tab, player, () => RestaurantInfo(player, restaurant));
            panel.AddInfo(Bold(restaurant.Name) + " " + Color(restaurant.Category, Colors.Grey));
            panel.AddWrapped(restaurant.Description);
            panel.AddInfo($"Note : {FormatRating(restaurant.Rating, restaurant.RatingCount)}");
            panel.AddInfo($"Commandes livrées : {restaurant.OrdersCount}");
            panel.AddInfo($"Gérant : {restaurant.OwnerName}");
            panel.AddInfo($"Distance : {Mathf.RoundToInt(Vector3.Distance(PositionOf(player), restaurant.Position))} m");
            panel.AddInfo($"Livraison jusqu'à vous : {FormatMoney(ComputeDeliveryFee(restaurant.Position, PositionOf(player)))}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskAddToCart(Player player, UERestaurant restaurant, UEMenuItem item)
        {
            string name = ItemName(player, item.ItemId);
            Panel panel = PanelHelper.Create($"Ajouter : {name}", UIPanel.PanelType.Input, player, () => AskAddToCart(player, restaurant, item));
            panel.TextLines.Add($"Quantité ? ({FormatMoney(item.Price)} l'unité, {item.Stock} dispo)");
            panel.SetInputPlaceholder("1");

            panel.PreviousButtonWithAction("Ajouter", () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) ? "1" : panel.inputText;
                if (!TryParseQuantity(input, out int quantity))
                {
                    player.Notify(AppName, "Quantité invalide.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }

                int characterId = player.character.Id;
                if (!_carts.TryGetValue(characterId, out Cart cart) || cart.RestaurantId != restaurant.Id)
                {
                    if (cart != null && cart.Count > 0)
                        player.Notify(AppName, "Votre ancien panier (autre restaurant) a été vidé.", NotificationManager.Type.Info, 5f);
                    cart = new Cart { RestaurantId = restaurant.Id };
                    _carts[characterId] = cart;
                }

                cart.Quantities.TryGetValue(item.Id, out int already);
                if (already + quantity > item.Stock)
                {
                    player.Notify(AppName, $"Stock insuffisant : {item.Stock - already} de plus maximum.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }

                cart.Quantities[item.Id] = already + quantity;
                player.Notify(AppName, $"{quantity}x {name} ajouté(s) au panier.", NotificationManager.Type.Success, 4f);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Panier et paiement
        // ------------------------------------------------------------------

        private async void CartMenu(Player player)
        {
            int characterId = player.character.Id;
            _carts.TryGetValue(characterId, out Cart cart);
            UERestaurant restaurant = cart != null && cart.Count > 0 ? await UERestaurant.Query(cart.RestaurantId) : null;

            if (restaurant == null)
            {
                _carts.Remove(characterId);
                Panel empty = PanelHelper.Create("Mon panier", UIPanel.PanelType.Tab, player, () => CartMenu(player));
                empty.AddInfo("Votre panier est vide.");
                empty.AddInfo(Color("Choisissez un restaurant pour commencer !", Colors.Grey));
                empty.NextButton("Restaurants", () => RestaurantsList(player));
                empty.PreviousButton();
                empty.CloseButton();
                empty.Display();
                return;
            }

            Dictionary<int, UEMenuItem> menu = (await UEMenuItem.Query(m => m.RestaurantId == restaurant.Id)).ToDictionary(m => m.Id);
            double subtotal = 0;

            Panel panel = PanelHelper.Create("Mon panier", UIPanel.PanelType.TabPrice, player, () => CartMenu(player));
            foreach (KeyValuePair<int, int> line in cart.Quantities.ToList())
            {
                if (!menu.TryGetValue(line.Key, out UEMenuItem item))
                {
                    cart.Quantities.Remove(line.Key);
                    continue;
                }
                subtotal += item.Price * line.Value;
                panel.AddTabLine($"{line.Value}x {ItemName(player, item.ItemId)}", FormatMoney(item.Price * line.Value), ItemUtils.GetIconIdByItemId(item.ItemId), _ =>
                {
                    // Sélectionner une ligne la retire du panier
                    cart.Quantities.Remove(line.Key);
                    player.Notify(AppName, "Article retiré du panier.", NotificationManager.Type.Info, 3f);
                    panel.Refresh();
                });
            }

            double fee = ComputeDeliveryFee(restaurant.Position, PositionOf(player));
            panel.AddTabLine(Color($"Livraison depuis {restaurant.Name}", Colors.Grey), FormatMoney(fee), DefaultIcon, _ => panel.Refresh());
            panel.AddTabLine(Bold("Total"), Bold(FormatMoney(subtotal + fee)), DefaultIcon, _ => panel.Refresh());
            panel.AddTabLine(Color("» Vider le panier", Colors.Error), "", DefaultIcon, _ =>
            {
                _carts.Remove(characterId);
                panel.Refresh();
            });

            panel.NextButton(Color("Commander", Colors.Success), () => Checkout(player));
            panel.AddButton("Retirer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void Checkout(Player player)
        {
            if (!_carts.TryGetValue(player.character.Id, out Cart cart) || cart.Count == 0) return;
            UERestaurant restaurant = await UERestaurant.Query(cart.RestaurantId);
            if (restaurant == null) return;

            Dictionary<int, UEMenuItem> menu = (await UEMenuItem.Query(m => m.RestaurantId == restaurant.Id)).ToDictionary(m => m.Id);
            double subtotal = cart.Quantities.Where(q => menu.ContainsKey(q.Key)).Sum(q => menu[q.Key].Price * q.Value);
            double fee = ComputeDeliveryFee(restaurant.Position, PositionOf(player));

            Panel panel = PanelHelper.Create("Valider la commande", UIPanel.PanelType.Input, player, () => Checkout(player));
            panel.TextLines.Add("Instructions pour le livreur (facultatif)");
            panel.SetInputPlaceholder("Ex : devant la mairie, sonnez 2 fois");

            panel.CloseButtonWithAction(Color($"Payer {FormatMoney(subtotal + fee)}", Colors.Success), () => PlaceOrder(player, panel.inputText));
            panel.TextLines.Add(Color($"Total : {FormatMoney(subtotal + fee)} - livré à votre position", Colors.Grey));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> PlaceOrder(Player player, string note)
        {
            int characterId = player.character.Id;
            if (!_carts.TryGetValue(characterId, out Cart cart) || cart.Count == 0) return false;

            UERestaurant restaurant = await UERestaurant.Query(cart.RestaurantId);
            if (restaurant == null || !restaurant.IsOpen || restaurant.IsSuspended)
            {
                player.Notify(AppName, "Ce restaurant vient de fermer.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (CanManage(player, restaurant))
            {
                player.Notify(AppName, "Vous ne pouvez pas commander dans votre propre restaurant.", NotificationManager.Type.Error, 5f);
                return false;
            }

            int active = (int)OrderStatus.Delivering;
            if ((await UEOrder.Query(o => o.ClientCharacterId == characterId && o.Status <= active)).Count >= Config.MaxActiveOrdersPerClient)
            {
                player.Notify(AppName, $"Maximum {Config.MaxActiveOrdersPerClient} commande(s) en cours à la fois.", NotificationManager.Type.Error, 5f);
                return false;
            }

            // Vérifie le stock au dernier moment (un autre client a pu commander entre-temps)
            List<(UEMenuItem item, int quantity)> lines = new List<(UEMenuItem, int)>();
            foreach (KeyValuePair<int, int> entry in cart.Quantities)
            {
                UEMenuItem item = await UEMenuItem.Query(entry.Key);
                if (item == null || item.RestaurantId != restaurant.Id || !item.IsAvailable || item.Stock < entry.Value)
                {
                    string name = item != null ? ItemName(player, item.ItemId) : "un article";
                    player.Notify(AppName, $"Stock insuffisant pour {name}. Modifiez votre panier.", NotificationManager.Type.Error, 6f);
                    return false;
                }
                lines.Add((item, entry.Value));
            }

            double subtotal = System.Math.Round(lines.Sum(l => l.item.Price * l.quantity), 2);
            if (subtotal < Config.MinOrderAmount)
            {
                player.Notify(AppName, $"Commande minimum : {FormatMoney(Config.MinOrderAmount)} de plats.", NotificationManager.Type.Error, 5f);
                return false;
            }

            Vector3 destination = PositionOf(player);
            double fee = ComputeDeliveryFee(restaurant.Position, destination);
            if (!Charge(player, subtotal + fee, $"{AppName} - {restaurant.Name}")) return false;

            foreach ((UEMenuItem item, int quantity) in lines)
            {
                item.Stock -= quantity;
                await item.Save();
            }

            long now = Now();
            UEOrder order = new UEOrder
            {
                ClientCharacterId = characterId,
                ClientName = player.FullName,
                RestaurantId = restaurant.Id,
                RestaurantName = restaurant.Name,
                Lines = lines.Select(l => new OrderLine { MenuItemId = l.item.Id, ItemId = l.item.ItemId, Quantity = l.quantity, UnitPrice = l.item.Price }).ToList(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Subtotal = subtotal,
                DeliveryFee = fee,
                State = restaurant.AutoAccept ? OrderStatus.Ready : OrderStatus.Pending,
                Destination = destination,
                CreatedAt = now,
                UpdatedAt = now
            };

            if (!await order.Save())
            {
                // Échec improbable de la base : on rend tout au client
                foreach ((UEMenuItem item, int quantity) in lines)
                {
                    item.Stock += quantity;
                    await item.Save();
                }
                await Pay(characterId, subtotal + fee, $"Remboursement {AppName}");
                return false;
            }

            _carts.Remove(characterId);
            player.Notify(AppName, $"Commande #{order.Id} payée ! Suivez-la dans « Mes commandes ».", NotificationManager.Type.Success, 6f);

            if (restaurant.AutoAccept)
            {
                NotifyRestaurant(restaurant, $"Commande #{order.Id} reçue et validée automatiquement.", NotificationManager.Type.Info, 6f);
                AlertCouriers(order, restaurant);
            }
            else
            {
                NotifyRestaurant(restaurant, $"Nouvelle commande #{order.Id} de {order.ClientName} ! Acceptez-la dans l'Espace restaurateur.", NotificationManager.Type.Warning, 10f);
            }
            return true;
        }

        // ------------------------------------------------------------------
        //  Suivi des commandes
        // ------------------------------------------------------------------

        private async void ClientOrders(Player player)
        {
            int characterId = player.character.Id;
            List<UEOrder> orders = (await UEOrder.Query(o => o.ClientCharacterId == characterId))
                .OrderByDescending(o => o.IsActive).ThenByDescending(o => o.CreatedAt).Take(15).ToList();

            Panel panel = PanelHelper.Create("Mes commandes", UIPanel.PanelType.Tab, player, () => ClientOrders(player));
            if (orders.Count == 0)
                panel.AddTabLine(Color("Aucune commande pour le moment", Colors.Grey), _ => { });

            foreach (UEOrder order in orders)
            {
                string rate = order.State == OrderStatus.Delivered && !order.Rated ? Color(" - à noter", Colors.Warning) : "";
                panel.AddTabLine($"#{order.Id} {order.RestaurantName} - {StatusLabel(order.State)}{rate}",
                    _ => ClientOrderDetails(player, order.Id));
            }

            if (orders.Count > 0) panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void ClientOrderDetails(Player player, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null) return;

            Panel panel = PanelHelper.Create($"Commande #{order.Id}", UIPanel.PanelType.Tab, player, () => ClientOrderDetails(player, orderId));
            panel.AddInfo($"{Bold(order.RestaurantName)} - {StatusLabel(order.State)}");
            panel.AddOrderLines(player, order);
            panel.AddInfo($"Total payé : {Bold(FormatMoney(order.Total))} (dont livraison {FormatMoney(order.DeliveryFee)})");
            if (order.Tip > 0) panel.AddInfo($"Pourboire : {FormatMoney(order.Tip)}");
            if (order.CourierCharacterId != 0)
                panel.AddInfo($"Livreur : {order.CourierName} ({order.CourierTransport})");
            else if (order.IsActive)
                panel.AddInfo(Color("Recherche d'un livreur…", Colors.Grey));
            if (order.IsActive)
                panel.AddInfo($"Adresse de livraison : à {Mathf.RoundToInt(Vector3.Distance(PositionOf(player), order.Destination))} m");
            if (!string.IsNullOrEmpty(order.Note))
            {
                panel.AddInfo("Instructions :");
                panel.AddWrapped(order.Note);
            }
            panel.AddInfo(Color($"Commandée {FormatAgo(order.CreatedAt)}", Colors.Grey));
            if (order.Rated)
                panel.AddInfo($"Vos notes : restaurant {order.RestaurantRating}/5, livreur {order.CourierRating}/5");

            if (order.IsActive)
            {
                panel.AddAction("Me livrer ici (ma position)", async _ =>
                {
                    UEOrder fresh = await UEOrder.Query(orderId);
                    if (fresh == null || !fresh.IsActive) return;
                    fresh.Destination = PositionOf(player);
                    await fresh.Save();
                    player.Notify(AppName, "Adresse de livraison mise à jour à votre position.", NotificationManager.Type.Success, 5f);

                    // Le GPS du livreur suit la nouvelle adresse
                    Player courier = fresh.State == OrderStatus.Delivering ? FindPlayer(fresh.CourierCharacterId) : null;
                    if (courier != null)
                    {
                        SetGps(courier, fresh.Destination, async p => await OnArrivedAtClient(p, orderId));
                        courier.Notify(AppName, "Le client a changé l'adresse de livraison, GPS mis à jour.", NotificationManager.Type.Info, 6f);
                    }
                    panel.Refresh();
                });
            }
            if (order.IsCancellable)
            {
                panel.AddAction(Color("Annuler (remboursement)", Colors.Error), async _ =>
                {
                    if (await CancelOrder(order, OrderStatus.Cancelled, "annulée par le client")) panel.Previous();
                    else panel.Refresh();
                });
            }
            if (order.State == OrderStatus.Delivered && !order.Rated)
                panel.AddAction(Color("Noter la commande", Colors.Warning), _ => RateRestaurant(player, orderId));

            if (order.IsActive || (order.State == OrderStatus.Delivered && !order.Rated))
                panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Notation : restaurant, puis livreur, puis pourboire
        // ------------------------------------------------------------------

        private void RateRestaurant(Player player, int orderId)
        {
            Panel panel = PanelHelper.Create("Noter le restaurant", UIPanel.PanelType.Tab, player, () => RateRestaurant(player, orderId));
            AddStarLines(panel, stars => RateCourier(player, orderId, stars));
            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RateCourier(Player player, int orderId, int restaurantStars)
        {
            Panel panel = PanelHelper.Create("Noter le livreur", UIPanel.PanelType.Tab, player, () => RateCourier(player, orderId, restaurantStars));
            AddStarLines(panel, stars => ChooseTip(player, orderId, restaurantStars, stars));
            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AddStarLines(Panel panel, System.Action<int> onSelect)
        {
            string[] labels = { "Excellent", "Très bien", "Correct", "Décevant", "Très mauvais" };
            for (int stars = 5; stars >= 1; stars--)
            {
                int value = stars;
                panel.AddTabLine($"{Color(value + "/5", Colors.Warning)}  {labels[5 - value]}", _ => onSelect(value));
            }
        }

        private void ChooseTip(Player player, int orderId, int restaurantStars, int courierStars)
        {
            Panel panel = PanelHelper.Create("Laisser un pourboire ?", UIPanel.PanelType.Tab, player, () => ChooseTip(player, orderId, restaurantStars, courierStars));
            foreach (double tip in new double[] { 0, 2, 5, 10, 20 })
            {
                string label = tip == 0 ? "Pas de pourboire" : $"Pourboire de {FormatMoney(tip)}";
                panel.AddTabLine(label, async _ =>
                {
                    if (await SubmitRating(player, orderId, restaurantStars, courierStars, tip)) panel.Close();
                    else panel.Refresh();
                });
            }
            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <returns>false si le pourboire n'a pas pu être payé (le joueur rechoisit).</returns>
        private async Task<bool> SubmitRating(Player player, int orderId, int restaurantStars, int courierStars, double tip)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.State != OrderStatus.Delivered || order.Rated) return true;
            if (tip > 0 && !Charge(player, tip, $"Pourboire {AppName}")) return false;

            order.Rated = true;
            order.RestaurantRating = restaurantStars;
            order.CourierRating = courierStars;
            order.Tip = tip;
            await order.Save();

            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant != null)
            {
                restaurant.RatingSum += restaurantStars;
                restaurant.RatingCount++;
                await restaurant.Save();
            }

            int courierId = order.CourierCharacterId;
            UECourier courier = (await UECourier.Query(c => c.CharacterId == courierId)).FirstOrDefault();
            if (courier != null)
            {
                courier.RatingSum += courierStars;
                courier.RatingCount++;
                courier.Tips += tip;
                courier.Earnings += tip;
                await courier.Save();
            }
            if (tip > 0) await Pay(courierId, tip, $"Pourboire de {order.ClientName}");
            FindPlayer(courierId)?.Notify(AppName, $"{order.ClientName} vous a donné {courierStars}/5 pour la course #{order.Id}.", NotificationManager.Type.Info, 6f);

            player.Notify(AppName, "Merci pour votre avis !", NotificationManager.Type.Success, 4f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Classement
        // ------------------------------------------------------------------

        private async void Leaderboard(Player player)
        {
            List<UECourier> couriers = (await UECourier.QueryAll())
                .Where(c => c.Deliveries > 0).OrderByDescending(c => c.Deliveries).ThenByDescending(c => c.Rating).Take(10).ToList();

            Panel panel = PanelHelper.Create("Classement des livreurs", UIPanel.PanelType.Tab, player, () => Leaderboard(player));
            if (couriers.Count == 0)
                panel.AddTabLine(Color("Personne n'a encore livré. À vous de jouer !", Colors.Grey), _ => { });

            for (int i = 0; i < couriers.Count; i++)
            {
                UECourier c = couriers[i];
                string rank = i == 0 ? Color("1er", "#FFD700") : i == 1 ? Color("2e", "#C0C0C0") : i == 2 ? Color("3e", "#CD7F32") : $"{i + 1}e";
                panel.AddTabLine($"{rank} {Bold(c.Name)} - {c.Deliveries} courses ({CourierLevel(c.Deliveries).name})", _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
