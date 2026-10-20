using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Utils;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>Côté restaurateur : ouvrir son restaurant, gérer la carte et le stock, traiter les commandes.</summary>
    public partial class UberEatsPlugin
    {
        private static readonly string[] Categories =
        {
            "Burgers", "Pizzas", "Tacos", "Kebab", "Sushi", "Asiatique", "Traditionnel",
            "Boulangerie", "Café", "Desserts", "Healthy", "Boissons", "Épicerie"
        };

        /// <summary>
        /// Restaurant géré par le joueur : celui de son entreprise quand le verrou métier est actif,
        /// sinon son restaurant personnel.
        /// </summary>
        private async Task<UERestaurant> GetOwnRestaurant(Player player)
        {
            if (Config.RestaurantRequiresJob)
            {
                if (!player.HasBiz) return null;
                int bizId = player.character.BizId;
                return (await UERestaurant.Query(r => r.BizId == bizId)).FirstOrDefault();
            }
            int characterId = player.character.Id;
            return (await UERestaurant.Query(r => r.OwnerCharacterId == characterId && r.BizId == 0)).FirstOrDefault();
        }

        private async void RestaurantSpace(Player player)
        {
            if (!IsRestaurateur(player))
            {
                player.Notify(AppName, "L'Espace restaurateur est réservé aux employés d'un restaurant partenaire.", NotificationManager.Type.Warning, 6f);
                return;
            }

            UERestaurant restaurant = await GetOwnRestaurant(player);
            if (restaurant == null)
            {
                Panel intro = PanelHelper.Create("Devenir partenaire", UIPanel.PanelType.Tab, player, () => RestaurantSpace(player));
                intro.AddInfo(Config.RestaurantRequiresJob
                    ? $"Inscrivez {Bold(player.biz?.BizName ?? "votre entreprise")} sur {Color(AppName, BrandColor)} !"
                    : $"Ouvrez votre restaurant sur {Color(AppName, BrandColor)} !");
                intro.AddInfo("Vous mettez vos plats en stock et fixez vos prix,");
                intro.AddInfo("les livreurs indépendants s'occupent du reste.");
                intro.AddInfo($"Inscription : {Bold(FormatMoney(Config.RestaurantCreationCost))}" + (Config.RestaurantRequiresJob ? " (entreprise)" : ""));
                intro.AddInfo($"Commission : {Config.CommissionPercent}% par commande");
                if (Config.RestaurantRequiresJob)
                    intro.AddInfo(Color("Recettes sur le compte de l'entreprise", Colors.Grey));
                intro.AddInfo(Color("Point de retrait placé à votre position", Colors.Grey));
                intro.NextButton(Color("Inscrire", Colors.Success), () => CreateRestaurant(player));
                intro.PreviousButton();
                intro.CloseButton();
                intro.Display();
                return;
            }

            int restaurantId = restaurant.Id;
            int pending = (await UEOrder.Query(o => o.RestaurantId == restaurantId))
                .Count(o => o.State == OrderStatus.Pending || o.State == OrderStatus.Preparing || o.State == OrderStatus.Ready);
            int dishes = (await UEMenuItem.Query(m => m.RestaurantId == restaurantId)).Count;

            Panel panel = PanelHelper.Create(restaurant.Name, UIPanel.PanelType.Tab, player, () => RestaurantSpace(player));
            if (restaurant.IsSuspended)
                panel.AddTabLine(Color("Restaurant suspendu par le staff", Colors.Error), _ => panel.Refresh());
            else
                panel.AddTabLine((restaurant.IsOpen ? Color("Ouvert", Colors.Success) + " - fermer" : Color("Fermé", Colors.Error) + " - ouvrir") + " le restaurant", async _ =>
                {
                    restaurant.IsOpen = !restaurant.IsOpen;
                    await restaurant.Save();
                    player.Notify(AppName, restaurant.IsOpen ? "Restaurant ouvert : les clients peuvent commander !" : "Restaurant fermé.", NotificationManager.Type.Success, 5f);
                    panel.Refresh();
                });
            panel.AddTabLine("Commandes à traiter" + Color($" ({pending})", pending > 0 ? Colors.Warning : Colors.Grey), _ => RestaurantOrders(player, restaurantId));
            panel.AddTabLine($"Ma carte ({dishes} plat(s))", _ => MenuManager(player, restaurantId));
            panel.AddTabLine("Historique", _ => RestaurantHistory(player, restaurantId));
            panel.AddTabLine("Statistiques", _ => RestaurantStats(player, restaurantId));
            panel.AddTabLine("Paramètres", _ => RestaurantSettings(player, restaurantId));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Création
        // ------------------------------------------------------------------

        private void CreateRestaurant(Player player)
        {
            Panel panel = PanelHelper.Create("Nom du restaurant", UIPanel.PanelType.Input, player, () => CreateRestaurant(player));
            panel.TextLines.Add("Quel est le nom de votre restaurant ?");
            panel.SetInputPlaceholder("Chez Loris");
            panel.NextButton("Suivant", () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 40)
                {
                    player.Notify(AppName, "Nom invalide (1 à 40 caractères).", NotificationManager.Type.Error, 5f);
                    CreateRestaurant(player);
                    return;
                }
                ChooseCategory(player, async (categoryPanel, category) =>
                {
                    if (await ConfirmCreateRestaurant(player, name, category)) categoryPanel.Close();
                });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ChooseCategory(Player player, Func<Panel, string, Task> onChosen)
        {
            Panel panel = PanelHelper.Create("Type de cuisine", UIPanel.PanelType.Tab, player, () => ChooseCategory(player, onChosen));
            foreach (string category in Categories)
                panel.AddTabLine(category, async _ => await onChosen(panel, category));
            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> ConfirmCreateRestaurant(Player player, string name, string category)
        {
            int characterId = player.character.Id;
            if (!IsRestaurateur(player)) return true;
            if (await GetOwnRestaurant(player) != null)
            {
                player.Notify(AppName, "Vous avez déjà un restaurant.", NotificationManager.Type.Error, 5f);
                return true;
            }

            Bizs biz = Config.RestaurantRequiresJob ? player.biz : null;
            if (biz != null)
            {
                if (biz.Bank < Config.RestaurantCreationCost)
                {
                    player.Notify(AppName, $"Le compte de l'entreprise doit contenir {FormatMoney(Config.RestaurantCreationCost)}.", NotificationManager.Type.Error, 6f);
                    return true;
                }
                biz.AddBankMoney(-Config.RestaurantCreationCost, $"{AppName} - inscription restaurant");
            }
            else if (!Charge(player, Config.RestaurantCreationCost, $"{AppName} - inscription restaurant")) return true;

            UERestaurant restaurant = new UERestaurant
            {
                OwnerCharacterId = characterId,
                BizId = biz?.Id ?? 0,
                OwnerName = player.FullName,
                Name = name,
                Category = category,
                Position = PositionOf(player),
                CreatedAt = Now()
            };
            if (!await restaurant.Save())
            {
                if (biz != null) biz.AddBankMoney(Config.RestaurantCreationCost, $"Remboursement inscription {AppName}");
                else await Pay(characterId, Config.RestaurantCreationCost, $"Remboursement inscription {AppName}");
                return true;
            }

            player.Notify(AppName, $"« {name} » est inscrit ! Rouvrez l'Espace restaurateur pour remplir votre carte.", NotificationManager.Type.Success, 8f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Commandes
        // ------------------------------------------------------------------

        private async void RestaurantOrders(Player player, int restaurantId)
        {
            List<UEOrder> orders = (await UEOrder.Query(o => o.RestaurantId == restaurantId))
                .Where(o => o.IsActive).OrderBy(o => o.CreatedAt).ToList();

            Panel panel = PanelHelper.Create("Commandes à traiter", UIPanel.PanelType.Tab, player, () => RestaurantOrders(player, restaurantId));
            if (orders.Count == 0)
                panel.AddTabLine(Color("Aucune commande en cours", Colors.Grey), _ => { });

            foreach (UEOrder order in orders)
            {
                panel.AddTabLine($"#{order.Id} {order.ClientName} - {order.ItemsCount} art. - {StatusLabel(order.State)}",
                    _ => RestaurantOrderDetails(player, order.Id));
            }

            if (orders.Count > 0) panel.NextButton("Traiter", () => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void RestaurantOrderDetails(Player player, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null) return;

            double payout = Math.Round(order.Subtotal * (1 - Config.CommissionPercent / 100.0), 2);

            Panel panel = PanelHelper.Create($"Commande #{order.Id} - cuisine", UIPanel.PanelType.Tab, player, () => RestaurantOrderDetails(player, orderId));
            panel.AddInfo($"Client : {Bold(order.ClientName)} ({FormatAgo(order.CreatedAt)})");
            panel.AddInfo($"État : {StatusLabel(order.State)}");
            panel.AddInfo("À préparer :");
            panel.AddOrderLines(player, order);
            panel.AddInfo($"Vous recevrez {Color(FormatMoney(payout), Colors.Success)} au retrait du sac");
            panel.AddInfo(order.CourierCharacterId != 0 ? $"Livreur : {order.CourierName} ({order.CourierTransport})" : Color("Pas encore de livreur", Colors.Grey));
            if (!string.IsNullOrEmpty(order.Note))
            {
                panel.AddInfo("Note du client :");
                panel.AddWrapped(order.Note);
            }

            void Step(string label, Func<Task<bool>> action)
            {
                panel.AddAction(label, async _ =>
                {
                    if (await action()) panel.Previous();
                    else panel.Refresh();
                });
            }

            if (order.State == OrderStatus.Pending)
            {
                Step(Color("Accepter (en cuisine)", Colors.Success), () => AdvanceOrder(player, orderId, OrderStatus.Preparing));
                Step(Color("Accepter, déjà prête", Colors.Info), () => AdvanceOrder(player, orderId, OrderStatus.Ready));
            }
            else if (order.State == OrderStatus.Preparing)
            {
                Step(Color("Commande prête", Colors.Success), () => AdvanceOrder(player, orderId, OrderStatus.Ready));
            }
            if (order.IsCancellable)
                Step(Color("Refuser (client remboursé)", Colors.Error), () => CancelOrder(order, OrderStatus.Refused, "refusée par le restaurant"));

            if (order.IsCancellable) panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> AdvanceOrder(Player player, int orderId, OrderStatus newState)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.Status >= (int)newState || !order.IsCancellable) return false;
            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant == null || !CanManage(player, restaurant)) return false;

            bool wasPending = order.State == OrderStatus.Pending;
            order.State = newState;
            order.UpdatedAt = Now();
            if (!await order.Save()) return false;

            Player client = FindPlayer(order.ClientCharacterId);
            if (newState == OrderStatus.Ready)
            {
                client?.Notify(AppName, $"Votre commande #{order.Id} est prête, un livreur va la récupérer.", NotificationManager.Type.Info, 6f);
                if (order.CourierCharacterId != 0)
                    FindPlayer(order.CourierCharacterId)?.Notify(AppName, $"La commande #{order.Id} est prête chez {restaurant.Name} !", NotificationManager.Type.Success, 6f);
            }
            else
            {
                client?.Notify(AppName, $"{restaurant.Name} a accepté votre commande #{order.Id}, c'est en cuisine !", NotificationManager.Type.Info, 6f);
            }

            if (wasPending) AlertCouriers(order, restaurant);
            return true;
        }

        private async void RestaurantHistory(Player player, int restaurantId)
        {
            List<UEOrder> orders = (await UEOrder.Query(o => o.RestaurantId == restaurantId))
                .Where(o => !o.IsActive).OrderByDescending(o => o.CreatedAt).Take(20).ToList();

            Panel panel = PanelHelper.Create("Historique du restaurant", UIPanel.PanelType.Tab, player, () => RestaurantHistory(player, restaurantId));
            if (orders.Count == 0)
                panel.AddTabLine(Color("Aucune commande terminée", Colors.Grey), _ => { });
            foreach (UEOrder order in orders)
            {
                string rating = order.Rated ? $" - {order.RestaurantRating}/5" : "";
                panel.AddTabLine($"#{order.Id} {order.ClientName} - {FormatMoney(order.Subtotal)} - {StatusLabel(order.State)} - {FormatAgo(order.CreatedAt)}{rating}", _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void RestaurantStats(Player player, int restaurantId)
        {
            UERestaurant restaurant = await UERestaurant.Query(restaurantId);
            if (restaurant == null) return;
            List<UEOrder> orders = await UEOrder.Query(o => o.RestaurantId == restaurantId);
            List<UEMenuItem> menu = await UEMenuItem.Query(m => m.RestaurantId == restaurantId);

            // Plat le plus vendu
            var best = orders.Where(o => o.State == OrderStatus.Delivered)
                .SelectMany(o => o.Lines).GroupBy(l => l.ItemId)
                .Select(g => new { ItemId = g.Key, Quantity = g.Sum(l => l.Quantity) })
                .OrderByDescending(x => x.Quantity).FirstOrDefault();

            Panel panel = PanelHelper.Create("Statistiques", UIPanel.PanelType.Tab, player, () => RestaurantStats(player, restaurantId));
            panel.AddInfo($"{Bold(restaurant.Name)} - {restaurant.Category}");
            panel.AddInfo($"Note : {FormatRating(restaurant.Rating, restaurant.RatingCount)}");
            panel.AddInfo($"Commandes livrées : {restaurant.OrdersCount}");
            panel.AddInfo($"Chiffre d'affaires net : {Color(FormatMoney(restaurant.Revenue), Colors.Success)}");
            panel.AddInfo($"Annulées / refusées : {orders.Count(o => o.State == OrderStatus.Cancelled || o.State == OrderStatus.Refused)}");
            if (best != null)
                panel.AddInfo($"Plat star : {ItemName(player, best.ItemId)} ({best.Quantity} vendus)");
            panel.AddInfo($"Plats à la carte : {menu.Count}");
            panel.AddInfo($"Articles en stock : {menu.Sum(m => m.Stock)}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Carte et stock
        // ------------------------------------------------------------------

        private async void MenuManager(Player player, int restaurantId)
        {
            List<UEMenuItem> items = (await UEMenuItem.Query(m => m.RestaurantId == restaurantId)).OrderBy(m => m.Price).ToList();

            Panel panel = PanelHelper.Create("Ma carte", UIPanel.PanelType.TabPrice, player, () => MenuManager(player, restaurantId));
            if (items.Count == 0)
                panel.AddTabLine("Carte vide", "", DefaultIcon, _ => panel.Refresh());

            foreach (UEMenuItem item in items)
            {
                string stock = item.Stock > 0 ? Color($"x{item.Stock}", Colors.Success) : Color("x0", Colors.Error);
                string hidden = item.IsAvailable ? "" : Color(" masqué", Colors.Grey);
                panel.AddTabLine($"{ItemName(player, item.ItemId)} {stock}{hidden}", FormatMoney(item.Price), ItemUtils.GetIconIdByItemId(item.ItemId),
                    _ => MenuItemDetails(player, item.Id));
            }

            if (items.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.NextButton(Color("Ajouter", Colors.Success), () => AddMenuItem(player, restaurantId));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AddMenuItem(Player player, int restaurantId)
        {
            HashSet<int> already = new HashSet<int>((await UEMenuItem.Query(m => m.RestaurantId == restaurantId)).Select(m => m.ItemId));
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            Panel panel = PanelHelper.Create("Ajouter un plat", UIPanel.PanelType.TabPrice, player, () => AddMenuItem(player, restaurantId));
            List<KeyValuePair<int, int>> candidates = inventory.Where(i => !already.Contains(i.Key)).ToList();
            if (candidates.Count == 0)
                panel.AddTabLine("Aucun nouvel objet dans votre inventaire", "", DefaultIcon, _ => panel.Refresh());

            foreach (KeyValuePair<int, int> slot in candidates)
            {
                int itemId = slot.Key;
                panel.AddTabLine(ItemName(player, itemId), $"x{slot.Value}", ItemUtils.GetIconIdByItemId(itemId),
                    _ => AskPrice(player, null, restaurantId, itemId));
            }

            if (candidates.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Saisie du prix : crée le plat (<paramref name="existing"/> null) ou modifie son prix.</summary>
        private void AskPrice(Player player, UEMenuItem existing, int restaurantId, int itemId)
        {
            string name = ItemName(player, itemId);
            Panel panel = PanelHelper.Create($"Prix : {name}", UIPanel.PanelType.Input, player, () => AskPrice(player, existing, restaurantId, itemId));
            panel.TextLines.Add($"Prix à l'unité (commission {Config.CommissionPercent}%)");
            panel.SetInputPlaceholder(existing != null ? existing.Price.ToString("0.00") : "10");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!TryParseAmount(panel.inputText, out double price) || price <= 0 || price > 100000)
                {
                    player.Notify(AppName, "Prix invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                UEMenuItem item = existing ?? new UEMenuItem { RestaurantId = restaurantId, ItemId = itemId, IsAvailable = true };
                item.Price = Math.Round(price, 2);
                if (!await item.Save()) return false;

                player.Notify(AppName, existing != null
                    ? $"Prix de {name} : {FormatMoney(item.Price)}."
                    : $"{name} ajouté à la carte. Déposez du stock pour le vendre !", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void MenuItemDetails(Player player, int menuItemId)
        {
            UEMenuItem item = await UEMenuItem.Query(menuItemId);
            if (item == null) return;
            string name = ItemName(player, item.ItemId);
            InventoryUtils.ReturnPlayerInventory(player).TryGetValue(item.ItemId, out int inInventory);

            Panel panel = PanelHelper.Create($"Plat : {name}", UIPanel.PanelType.Tab, player, () => MenuItemDetails(player, menuItemId));
            panel.AddTabLine($"Déposer du stock (vous en avez {inInventory})", _ => AskStock(player, item, true));
            panel.AddTabLine($"Reprendre du stock (stock : {item.Stock})", _ => AskStock(player, item, false));
            panel.AddTabLine($"Modifier le prix ({FormatMoney(item.Price)})", _ => AskPrice(player, item, item.RestaurantId, item.ItemId));
            panel.AddTabLine(item.IsAvailable ? "Masquer de la carte" : "Afficher sur la carte", async _ =>
            {
                item.IsAvailable = !item.IsAvailable;
                await item.Save();
                panel.Refresh();
            });
            panel.AddTabLine(Color("Retirer de la carte", Colors.Error), async _ =>
            {
                if (item.Stock > 0 && !InventoryUtils.AddItem(player, item.ItemId, item.Stock))
                {
                    player.Notify(AppName, "Inventaire plein : impossible de reprendre le stock restant.", NotificationManager.Type.Error, 5f);
                    return;
                }
                await item.Delete();
                player.Notify(AppName, $"{name} retiré de la carte" + (item.Stock > 0 ? $", {item.Stock} rendu(s) dans votre inventaire." : "."), NotificationManager.Type.Success, 5f);
                panel.Previous();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskStock(Player player, UEMenuItem item, bool deposit)
        {
            string name = ItemName(player, item.ItemId);
            Panel panel = PanelHelper.Create(deposit ? $"Déposer : {name}" : $"Reprendre : {name}", UIPanel.PanelType.Input, player, () => AskStock(player, item, deposit));
            panel.TextLines.Add(deposit ? "Quantité à mettre en vente" : $"Quantité à reprendre (stock : {item.Stock})");
            panel.SetInputPlaceholder("Quantité");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!TryParseQuantity(panel.inputText, out int quantity))
                {
                    player.Notify(AppName, "Quantité invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                UEMenuItem fresh = await UEMenuItem.Query(item.Id);
                if (fresh == null) return false;

                if (deposit)
                {
                    if (!InventoryUtils.CheckInventoryContainsItem(player, fresh.ItemId, quantity))
                    {
                        player.Notify(AppName, "Vous n'en avez pas autant.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    int removed = InventoryUtils.RemoveFromInventory(player, fresh.ItemId, quantity);
                    if (removed <= 0) return false;
                    fresh.Stock += removed;
                    await fresh.Save();
                    player.Notify(AppName, $"{removed} {name} en vente. Stock : {fresh.Stock}.", NotificationManager.Type.Success, 5f);
                }
                else
                {
                    if (quantity > fresh.Stock)
                    {
                        player.Notify(AppName, $"Il n'y a que {fresh.Stock} en stock.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    fresh.Stock -= quantity;
                    if (!await fresh.Save()) return false;
                    if (!InventoryUtils.AddItem(player, fresh.ItemId, quantity))
                    {
                        fresh.Stock += quantity;
                        await fresh.Save();
                        player.Notify(AppName, "Inventaire plein.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    player.Notify(AppName, $"{quantity} {name} récupéré(s). Stock : {fresh.Stock}.", NotificationManager.Type.Success, 5f);
                }
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Paramètres
        // ------------------------------------------------------------------

        private async void RestaurantSettings(Player player, int restaurantId)
        {
            UERestaurant restaurant = await UERestaurant.Query(restaurantId);
            if (restaurant == null) return;

            Panel panel = PanelHelper.Create("Paramètres du restaurant", UIPanel.PanelType.Tab, player, () => RestaurantSettings(player, restaurantId));
            panel.AddTabLine($"Nom : {restaurant.Name}", _ => AskRestaurantText(player, restaurantId, true));
            panel.AddTabLine("Description : " + (string.IsNullOrEmpty(restaurant.Description) ? Color("aucune", Colors.Grey) : Truncate(restaurant.Description, 30)),
                _ => AskRestaurantText(player, restaurantId, false));
            panel.AddTabLine($"Type de cuisine : {restaurant.Category}", _ => ChooseCategory(player, async (categoryPanel, category) =>
            {
                UERestaurant fresh = await UERestaurant.Query(restaurantId);
                if (fresh == null) return;
                fresh.Category = category;
                await fresh.Save();
                categoryPanel.Previous();
            }));
            panel.AddTabLine("Acceptation automatique : " + (restaurant.AutoAccept ? Color("activée", Colors.Success) : Color("désactivée", Colors.Grey)), async _ =>
            {
                restaurant.AutoAccept = !restaurant.AutoAccept;
                await restaurant.Save();
                player.Notify(AppName, restaurant.AutoAccept
                    ? "Les commandes seront prêtes dès le paiement (idéal quand vous n'êtes pas en cuisine)."
                    : "Vous validerez chaque commande vous-même.", NotificationManager.Type.Info, 6f);
                panel.Refresh();
            });
            panel.AddTabLine("Placer le point de retrait ici", async _ =>
            {
                restaurant.Position = PositionOf(player);
                await restaurant.Save();
                player.Notify(AppName, "Point de retrait déplacé à votre position.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddTabLine(Color("Fermer définitivement le restaurant", Colors.Error), _ => ConfirmDeleteRestaurant(player, restaurantId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskRestaurantText(Player player, int restaurantId, bool isName)
        {
            Panel panel = PanelHelper.Create(isName ? "Renommer le restaurant" : "Description du restaurant", UIPanel.PanelType.Input, player, () => AskRestaurantText(player, restaurantId, isName));
            panel.TextLines.Add(isName ? "Nouveau nom (40 caractères max)" : "Une phrase (120 caractères max)");
            panel.SetInputPlaceholder(isName ? "Chez Loris" : "Les meilleurs burgers d'Amboise !");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string text = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length > (isName ? 40 : 120))
                {
                    player.Notify(AppName, "Texte invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                UERestaurant restaurant = await UERestaurant.Query(restaurantId);
                if (restaurant == null) return false;
                if (isName) restaurant.Name = text;
                else restaurant.Description = text;
                return await restaurant.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ConfirmDeleteRestaurant(Player player, int restaurantId)
        {
            Panel panel = PanelHelper.Create("Fermeture définitive", UIPanel.PanelType.Tab, player, () => ConfirmDeleteRestaurant(player, restaurantId));
            panel.AddInfo(Color("Le restaurant sera supprimé.", Colors.Error));
            panel.AddInfo("Le stock restant vous est rendu.");
            panel.AddInfo("L'inscription n'est pas remboursée.");

            panel.CloseButtonWithAction(Color("Confirmer", Colors.Error), async () =>
            {
                if (!await DeleteRestaurant(restaurantId, player)) return false;
                player.Notify(AppName, "Restaurant fermé définitivement.", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>
        /// Supprime un restaurant et sa carte. Le stock est rendu à <paramref name="stockReceiver"/>
        /// (null = stock détruit, utilisé par le staff). Refusé s'il reste des commandes en cours.
        /// </summary>
        private async Task<bool> DeleteRestaurant(int restaurantId, Player stockReceiver)
        {
            UERestaurant restaurant = await UERestaurant.Query(restaurantId);
            if (restaurant == null) return false;

            if ((await UEOrder.Query(o => o.RestaurantId == restaurantId)).Any(o => o.IsActive))
            {
                stockReceiver?.Notify(AppName, "Des commandes sont encore en cours : traitez-les d'abord.", NotificationManager.Type.Error, 6f);
                return false;
            }

            List<UEMenuItem> items = await UEMenuItem.Query(m => m.RestaurantId == restaurantId);
            if (stockReceiver != null)
            {
                foreach (UEMenuItem item in items.Where(i => i.Stock > 0))
                {
                    if (!InventoryUtils.AddItem(stockReceiver, item.ItemId, item.Stock))
                    {
                        stockReceiver.Notify(AppName, "Inventaire plein : reprenez votre stock petit à petit depuis « Ma carte ».", NotificationManager.Type.Error, 6f);
                        return false;
                    }
                    item.Stock = 0;
                    await item.Save();
                }
            }

            foreach (UEMenuItem item in items)
                await item.Delete();
            return await restaurant.Delete();
        }
    }
}
