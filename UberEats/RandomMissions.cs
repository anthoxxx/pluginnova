using System;
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
    /// <summary>
    /// Courses aléatoires : le livreur récupère un colis sur un point de livraison et le dépose sur un
    /// autre, tirés au hasard parmi les points placés par le staff. Aucun restaurant n'est concerné,
    /// la plateforme paie selon la distance.
    /// </summary>
    public partial class UberEatsPlugin
    {
        private class RandomMission
        {
            public UEDeliveryPoint Pickup;
            public UEDeliveryPoint Dropoff;
            public double Pay;
            public bool PickedUp;
        }

        private readonly Dictionary<int, RandomMission> _missions = new Dictionary<int, RandomMission>();

        /// <summary>Fin de la dernière course aléatoire (pause entre deux courses).</summary>
        private readonly Dictionary<int, long> _lastMission = new Dictionary<int, long>();

        private void RandomMissionMenu(Player player)
        {
            int characterId = player.character.Id;
            _missions.TryGetValue(characterId, out RandomMission mission);

            Panel panel = PanelHelper.Create("Courses aléatoires", UIPanel.PanelType.Tab, player, () => RandomMissionMenu(player));
            panel.AddInfo(Color("Colis entre deux points de la ville,", Colors.Grey));
            panel.AddInfo(Color("payé par la plateforme selon la distance.", Colors.Grey));

            if (mission == null)
            {
                panel.AddAction(Color("Lancer une course", Colors.Success), async _ =>
                {
                    await StartRandomMission(player);
                    panel.Refresh();
                });
            }
            else
            {
                UEDeliveryPoint target = mission.PickedUp ? mission.Dropoff : mission.Pickup;
                panel.AddInfo($"Étape : {(mission.PickedUp ? "déposer le colis" : "récupérer le colis")}");
                panel.AddInfo($"Point : {target.Name} ({Mathf.RoundToInt(Vector3.Distance(PositionOf(player), target.Position))} m)");
                panel.AddInfo($"Paie : {Color(FormatMoney(mission.Pay), Colors.Success)}");
                panel.AddAction("Relancer le GPS", _ =>
                {
                    SetMissionGps(player, mission);
                    player.Notify(AppName, "GPS relancé.", NotificationManager.Type.Info, 4f);
                    panel.Refresh();
                });
                panel.AddAction(Color("Abandonner la course", Colors.Error), _ =>
                {
                    _missions.Remove(characterId);
                    ClearGps(player);
                    Alert(player, "Course aléatoire abandonnée.", NotificationManager.Type.Warning, 5f);
                    panel.Refresh();
                });
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task StartRandomMission(Player player)
        {
            int characterId = player.character.Id;
            if (_missions.ContainsKey(characterId)) return;
            if (await GetCurrentJob(characterId) != null)
            {
                player.Notify(AppName, "Terminez d'abord votre course en cours.", NotificationManager.Type.Warning, 5f);
                return;
            }
            if (_lastMission.TryGetValue(characterId, out long last) && Now() - last < Config.RandomMissionCooldownSeconds)
            {
                player.Notify(AppName, $"Petite pause : nouvelle course dans {Config.RandomMissionCooldownSeconds - (Now() - last)} s.", NotificationManager.Type.Info, 5f);
                return;
            }

            List<UEDeliveryPoint> points = await UEDeliveryPoint.QueryAll();
            if (points.Count < 2)
            {
                player.Notify(AppName, "Pas assez de points de livraison : le staff doit en placer au moins 2.", NotificationManager.Type.Warning, 6f);
                return;
            }

            System.Random random = new System.Random();
            UEDeliveryPoint pickup = points[random.Next(points.Count)];
            List<UEDeliveryPoint> others = points.Where(p => p.Id != pickup.Id).ToList();
            List<UEDeliveryPoint> far = others.Where(p => Vector3.Distance(p.Position, pickup.Position) >= Config.RandomMissionMinDistance).ToList();
            List<UEDeliveryPoint> candidates = far.Count > 0 ? far : others;
            UEDeliveryPoint dropoff = candidates[random.Next(candidates.Count)];

            double pay = Config.RandomMissionBasePay + Config.RandomMissionPer100m * (Vector3.Distance(pickup.Position, dropoff.Position) / 100f);
            RandomMission mission = new RandomMission
            {
                Pickup = pickup,
                Dropoff = dropoff,
                Pay = Math.Round(Math.Min(pay, Config.RandomMissionMaxPay), 2)
            };
            _missions[characterId] = mission;
            _onDuty.Add(characterId);

            SetMissionGps(player, mission);
            Alert(player, $"Course aléatoire : récupérez le colis à « {pickup.Name} » puis livrez-le à « {dropoff.Name} » ({FormatMoney(mission.Pay)}).", NotificationManager.Type.Success);
        }

        private void SetMissionGps(Player player, RandomMission mission)
        {
            UEDeliveryPoint target = mission.PickedUp ? mission.Dropoff : mission.Pickup;
            SetGps(player, target.Position, async p => await OnMissionPoint(p));
        }

        private async Task OnMissionPoint(Player player)
        {
            int characterId = player.character.Id;
            if (!_missions.TryGetValue(characterId, out RandomMission mission)) return;

            if (!mission.PickedUp)
            {
                mission.PickedUp = true;
                Alert(player, $"Colis récupéré ! Livrez-le à « {mission.Dropoff.Name} ».", NotificationManager.Type.Success, 6f);
                SetMissionGps(player, mission);
                return;
            }

            _missions.Remove(characterId);
            _lastMission[characterId] = Now();

            UECourier profile = await GetOrCreateCourier(player);
            profile.RandomDeliveries++;
            profile.Earnings += mission.Pay;
            profile.Name = player.FullName;
            await profile.Save();
            await Pay(characterId, mission.Pay, $"{AppName} - course aléatoire");
            Alert(player, "Colis livré ! Lancez une autre course depuis l'Espace livreur.", NotificationManager.Type.Success, 6f);
        }

        // ------------------------------------------------------------------
        //  Staff : points de livraison aléatoires
        // ------------------------------------------------------------------

        private async void AdminDeliveryPoints(Player player)
        {
            List<UEDeliveryPoint> points = (await UEDeliveryPoint.QueryAll()).OrderBy(p => p.Name).ToList();
            Vector3 here = PositionOf(player);

            Panel panel = PanelHelper.Create($"{AppName} - Points de livraison", UIPanel.PanelType.Tab, player, () => AdminDeliveryPoints(player));
            panel.AddAction(Color("Placer un point ici", Colors.Success), _ => AdminNewDeliveryPoint(player));
            if (points.Count < 2)
                panel.AddInfo(Color("Placez au moins 2 points pour les courses", Colors.Warning));
            foreach (UEDeliveryPoint point in points)
                panel.AddTabLine($"[{point.Id}] {point.Name} - {Mathf.RoundToInt(Vector3.Distance(here, point.Position))} m", _ => AdminDeliveryPoint(player, point.Id));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminNewDeliveryPoint(Player player)
        {
            Panel panel = PanelHelper.Create($"{AppName} - Nouveau point", UIPanel.PanelType.Input, player, () => AdminNewDeliveryPoint(player));
            panel.TextLines.Add("Nom du point (placé à votre position)");
            panel.SetInputPlaceholder("Ex : Parking de la gare");
            panel.PreviousButtonWithAction("Placer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 40)
                {
                    player.Notify(AppName, "Nom invalide (1 à 40 caractères).", NotificationManager.Type.Error, 5f);
                    return false;
                }
                bool saved = await new UEDeliveryPoint { Name = name, Position = PositionOf(player) }.Save();
                if (saved) player.Notify(AppName, $"Point « {name} » placé.", NotificationManager.Type.Success, 5f);
                return saved;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminDeliveryPoint(Player player, int pointId)
        {
            UEDeliveryPoint point = await UEDeliveryPoint.Query(pointId);
            if (point == null) return;

            Panel panel = PanelHelper.Create($"Point de livraison [{point.Id}]", UIPanel.PanelType.Tab, player, () => AdminDeliveryPoint(player, pointId));
            panel.AddInfo($"{Bold(point.Name)} - à {Mathf.RoundToInt(Vector3.Distance(PositionOf(player), point.Position))} m");
            panel.AddAction("Se téléporter", _ =>
            {
                player.setup.TargetSetPosition(point.Position);
                panel.Refresh();
            });
            panel.AddAction("Déplacer ici", async _ =>
            {
                point.Position = PositionOf(player);
                await point.Save();
                player.Notify(AppName, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddAction(Color("Supprimer", Colors.Error), async _ =>
            {
                await point.Delete();
                player.Notify(AppName, $"Point « {point.Name} » supprimé.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
