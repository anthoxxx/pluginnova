using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>Côté livreur indépendant : prendre son service, accepter des courses, récupérer et livrer.</summary>
    public partial class UberEatsPlugin
    {
        private static readonly string[] Transports = { "Voiture", "À pied" };

        private async Task<UEOrder> GetCurrentJob(int characterId)
        {
            int delivering = (int)OrderStatus.Delivering;
            return (await UEOrder.Query(o => o.CourierCharacterId == characterId && o.Status <= delivering)).FirstOrDefault();
        }

        private async void CourierMenu(Player player)
        {
            int characterId = player.character.Id;
            UECourier profile = await GetOrCreateCourier(player);
            UEOrder job = await GetCurrentJob(characterId);
            bool onDuty = _onDuty.Contains(characterId);
            int available = onDuty ? (await GetAvailableJobs(characterId)).Count : 0;

            Panel panel = PanelHelper.Create("Espace livreur", UIPanel.PanelType.Tab, player, () => CourierMenu(player));
            if (onDuty)
            {
                panel.AddTabLine(Color("En service", Colors.Success) + " - terminer mon service", _ =>
                {
                    _onDuty.Remove(characterId);
                    player.Notify(AppName, "Fin de service. À bientôt !", NotificationManager.Type.Info, 4f);
                    panel.Refresh();
                });
            }
            else
            {
                panel.AddTabLine(Color("Hors service", Colors.Grey) + " - prendre mon service", _ => ChooseTransport(player, true));
            }

            if (job != null)
                panel.AddTabLine(Color($"Ma course en cours : #{job.Id} {job.RestaurantName}", Colors.Warning), _ => CurrentJob(player));
            panel.AddTabLine($"Courses disponibles" + (onDuty ? Color($" ({available})", available > 0 ? Colors.Success : Colors.Grey) : ""), _ => AvailableJobs(player));
            panel.AddTabLine("Mon profil", _ => CourierProfile(player));
            panel.AddTabLine($"Moyen de transport : {profile.Transport}", _ => ChooseTransport(player, false));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ChooseTransport(Player player, bool startDuty)
        {
            Panel panel = PanelHelper.Create("Moyen de transport", UIPanel.PanelType.Tab, player, () => ChooseTransport(player, startDuty));
            foreach (string transport in Transports)
            {
                panel.AddTabLine(transport, async _ =>
                {
                    UECourier profile = await GetOrCreateCourier(player);
                    profile.Transport = transport;
                    profile.Name = player.FullName;
                    await profile.Save();

                    if (startDuty)
                    {
                        _onDuty.Add(player.character.Id);
                        int count = (await GetAvailableJobs(player.character.Id)).Count;
                        player.Notify(AppName, $"Vous êtes en service ({transport}). {count} course(s) disponible(s).", NotificationManager.Type.Success, 6f);
                    }
                    panel.Previous();
                });
            }
            panel.AddButton("Valider", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void CourierProfile(Player player)
        {
            UECourier profile = await GetOrCreateCourier(player);
            (string name, double bonus, int next) level = CourierLevel(profile.Deliveries);

            Panel panel = PanelHelper.Create("Mon profil livreur", UIPanel.PanelType.Tab, player, () => CourierProfile(player));
            panel.AddInfo($"{Bold(player.FullName)} - {Color(level.name, BrandColor)}");
            if (level.bonus > 0) panel.AddInfo(Color($"Bonus : +{level.bonus}% par course", Colors.Success));
            panel.AddInfo(level.next > 0 ? $"Prochain niveau dans {level.next - profile.Deliveries} course(s)" : Color("Niveau maximum atteint !", Colors.Warning));
            panel.AddInfo($"Courses effectuées : {profile.Deliveries}");
            panel.AddInfo($"Note moyenne : {FormatRating(profile.Rating, profile.RatingCount)}");
            panel.AddInfo($"Gains totaux : {FormatMoney(profile.Earnings)}");
            panel.AddInfo($"Dont pourboires : {FormatMoney(profile.Tips)}");
            panel.AddInfo($"Transport : {profile.Transport}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Courses disponibles
        // ------------------------------------------------------------------

        private static async Task<List<UEOrder>> GetAvailableJobs(int courierCharacterId)
        {
            int preparing = (int)OrderStatus.Preparing, ready = (int)OrderStatus.Ready;
            return (await UEOrder.Query(o => o.CourierCharacterId == 0 && (o.Status == preparing || o.Status == ready)))
                .Where(o => o.ClientCharacterId != courierCharacterId).ToList();
        }

        private async void AvailableJobs(Player player)
        {
            int characterId = player.character.Id;
            if (!_onDuty.Contains(characterId))
            {
                player.Notify(AppName, "Prenez votre service pour voir les courses.", NotificationManager.Type.Warning, 5f);
                CourierMenu(player);
                return;
            }

            List<UEOrder> jobs = await GetAvailableJobs(characterId);
            Dictionary<int, UERestaurant> restaurants = (await UERestaurant.QueryAll()).ToDictionary(r => r.Id);
            Vector3 here = PositionOf(player);

            Panel panel = PanelHelper.Create("Courses disponibles", UIPanel.PanelType.Tab, player, () => AvailableJobs(player));
            if (jobs.Count == 0)
                panel.AddTabLine(Color("Aucune course pour le moment, restez en service !", Colors.Grey), _ => { });

            foreach (UEOrder job in jobs.Where(j => restaurants.ContainsKey(j.RestaurantId))
                         .OrderBy(j => Vector3.Distance(here, restaurants[j.RestaurantId].Position)))
            {
                UERestaurant restaurant = restaurants[job.RestaurantId];
                int toRestaurant = Mathf.RoundToInt(Vector3.Distance(here, restaurant.Position));
                int toClient = Mathf.RoundToInt(Vector3.Distance(restaurant.Position, job.Destination));
                string state = job.State == OrderStatus.Ready ? Color("prête", Colors.Success) : Color("en cuisine", Colors.Orange);
                panel.AddTabLine($"{restaurant.Name} ({toRestaurant} m) > client ({toClient} m) - {Color(FormatMoney(job.DeliveryFee), Colors.Success)} - {state}",
                    _ => JobDetails(player, job.Id));
            }

            if (jobs.Count > 0) panel.NextButton("Détails", () => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void JobDetails(Player player, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            UERestaurant restaurant = order != null ? await UERestaurant.Query(order.RestaurantId) : null;
            if (order == null || restaurant == null || !order.IsAvailableForCourier)
            {
                player.Notify(AppName, "Cette course n'est plus disponible.", NotificationManager.Type.Warning, 5f);
                return;
            }

            UECourier profile = await GetOrCreateCourier(player);
            double bonus = CourierLevel(profile.Deliveries).bonus;
            Vector3 here = PositionOf(player);

            Panel panel = PanelHelper.Create($"Course #{order.Id}", UIPanel.PanelType.Tab, player, () => JobDetails(player, orderId));
            panel.AddInfo($"Retrait : {Bold(restaurant.Name)} ({StatusLabel(order.State)})");
            panel.AddInfo($"   à {Mathf.RoundToInt(Vector3.Distance(here, restaurant.Position))} m de vous");
            panel.AddInfo($"Client : {order.ClientName}");
            panel.AddInfo($"   à {Mathf.RoundToInt(Vector3.Distance(restaurant.Position, order.Destination))} m du restaurant");
            panel.AddInfo($"Sac : {order.ItemsCount} article(s)");
            panel.AddInfo($"Gain : {Color(FormatMoney(order.DeliveryFee * (1 + bonus / 100.0)), Colors.Success)}" + (bonus > 0 ? $" (bonus +{bonus}%)" : "") + " + pourboire");
            if (!string.IsNullOrEmpty(order.Note))
            {
                panel.AddInfo("Instructions :");
                panel.AddWrapped(order.Note);
            }

            panel.CloseButtonWithAction(Color("Accepter", Colors.Success), () => AcceptJob(player, orderId));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> AcceptJob(Player player, int orderId)
        {
            int characterId = player.character.Id;
            if (!_onDuty.Contains(characterId)) return false;
            if (await GetCurrentJob(characterId) != null)
            {
                player.Notify(AppName, "Terminez d'abord votre course en cours.", NotificationManager.Type.Warning, 5f);
                return false;
            }

            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || !order.IsAvailableForCourier)
            {
                player.Notify(AppName, "Trop tard, un autre livreur a pris cette course !", NotificationManager.Type.Warning, 5f);
                return false;
            }
            if (order.ClientCharacterId == characterId) return false;
            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant == null) return false;

            UECourier profile = await GetOrCreateCourier(player);
            order.CourierCharacterId = characterId;
            order.CourierName = player.FullName;
            order.CourierTransport = profile.Transport;
            order.UpdatedAt = Now();
            if (!await order.Save()) return false;

            SetGps(player, restaurant.Position, async p => await TryPickup(p, orderId));
            player.Notify(AppName, $"Course acceptée ! Rendez-vous chez {restaurant.Name} (point sur votre carte).", NotificationManager.Type.Success, 6f);
            FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"{player.FullName} ({profile.Transport}) va livrer votre commande #{order.Id}.", NotificationManager.Type.Info, 6f);
            NotifyRestaurant(restaurant, $"Le livreur {player.FullName} arrive pour la commande #{order.Id}.", NotificationManager.Type.Info, 6f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Course en cours
        // ------------------------------------------------------------------

        private async void CurrentJob(Player player)
        {
            UEOrder order = await GetCurrentJob(player.character.Id);
            if (order == null)
            {
                player.Notify(AppName, "Aucune course en cours.", NotificationManager.Type.Info, 4f);
                return;
            }
            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            Vector3 here = PositionOf(player);
            int orderId = order.Id;

            Panel panel = PanelHelper.Create($"Ma course #{order.Id}", UIPanel.PanelType.Tab, player, () => CurrentJob(player));
            panel.AddInfo($"{Bold(order.RestaurantName)} > {Bold(order.ClientName)}");
            panel.AddInfo($"État : {StatusLabel(order.State)}");
            panel.AddOrderLines(player, order);
            if (!string.IsNullOrEmpty(order.Note))
            {
                panel.AddInfo("Instructions :");
                panel.AddWrapped(order.Note);
            }

            if (order.State != OrderStatus.Delivering)
            {
                if (restaurant != null)
                    panel.AddInfo($"Restaurant à {Mathf.RoundToInt(Vector3.Distance(here, restaurant.Position))} m");
                panel.AddAction(Color("Récupérer le sac", Colors.Success), async _ =>
                {
                    await TryPickup(player, orderId);
                    panel.Refresh();
                });
                panel.AddAction("GPS vers le restaurant", _ =>
                {
                    if (restaurant != null)
                    {
                        SetGps(player, restaurant.Position, async p => await TryPickup(p, orderId));
                        player.Notify(AppName, "Point GPS placé sur le restaurant.", NotificationManager.Type.Info, 4f);
                    }
                    panel.Refresh();
                });
                panel.AddAction(Color("Abandonner la course", Colors.Error), async _ =>
                {
                    if (await AbandonJob(player, orderId)) panel.Previous();
                    else panel.Refresh();
                });
            }
            else
            {
                panel.AddInfo($"Adresse de livraison à {Mathf.RoundToInt(Vector3.Distance(here, order.Destination))} m");
                Player client = FindPlayer(order.ClientCharacterId);
                panel.AddInfo(client != null
                    ? $"Le client est à {Mathf.RoundToInt(Vector3.Distance(here, PositionOf(client)))} m"
                    : Color("Le client n'est pas en ville", Colors.Error));

                panel.AddAction(Color("Remettre le sac", Colors.Success), async _ =>
                {
                    if (!await TryHandover(player, orderId)) panel.Refresh();
                });
                panel.AddAction("GPS vers le client", _ =>
                {
                    SetGps(player, order.Destination, async p => await OnArrivedAtClient(p, orderId));
                    player.Notify(AppName, "Point GPS placé sur l'adresse de livraison.", NotificationManager.Type.Info, 4f);
                    panel.Refresh();
                });
                panel.AddAction("Prévenir le client", _ =>
                {
                    FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"Votre livreur {player.FullName} vous attend avec votre commande !", NotificationManager.Type.Warning, 10f);
                    player.Notify(AppName, "Le client a été prévenu.", NotificationManager.Type.Info, 4f);
                    panel.Refresh();
                });
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Rend la course aux autres livreurs (uniquement avant d'avoir récupéré le sac).</summary>
        private async Task<bool> AbandonJob(Player player, int orderId)
        {
            UEOrder order = await UEOrder.Query(orderId);
            if (order == null || order.CourierCharacterId != player.character.Id || order.State == OrderStatus.Delivering)
                return false;

            order.CourierCharacterId = 0;
            order.CourierName = null;
            order.CourierTransport = null;
            order.UpdatedAt = Now();
            if (!await order.Save()) return false;

            ClearGps(player);
            player.Notify(AppName, "Course abandonnée.", NotificationManager.Type.Info, 4f);
            FindPlayer(order.ClientCharacterId)?.Notify(AppName, $"Votre livreur s'est désisté, nous cherchons quelqu'un d'autre pour la commande #{order.Id}.", NotificationManager.Type.Warning, 6f);

            UERestaurant restaurant = await UERestaurant.Query(order.RestaurantId);
            if (restaurant != null) AlertCouriers(order, restaurant);
            return true;
        }
    }
}
