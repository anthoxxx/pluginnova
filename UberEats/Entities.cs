using System.Collections.Generic;
using System.Linq;
using ModKit.ORM;
using Newtonsoft.Json;
using SQLite;
using UnityEngine;

namespace UberEats
{
    /// <summary>Étapes d'une commande, de la validation du client jusqu'à la remise en main propre.</summary>
    public enum OrderStatus
    {
        /// <summary>Payée, en attente de validation par le restaurateur.</summary>
        Pending = 0,
        /// <summary>Acceptée, en cuisine.</summary>
        Preparing = 1,
        /// <summary>Prête, à récupérer par un livreur.</summary>
        Ready = 2,
        /// <summary>Récupérée par le livreur, en route vers le client.</summary>
        Delivering = 3,
        Delivered = 4,
        Cancelled = 5,
        Refused = 6
    }

    /// <summary>Restaurant inscrit sur la plateforme. Un joueur = un restaurant (configurable).</summary>
    public class UERestaurant : ModEntity<UERestaurant>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int OwnerCharacterId { get; set; }
        public string OwnerName { get; set; }
        /// <summary>Entreprise propriétaire (0 = restaurant personnel, créé sans le verrou métier).</summary>
        public int BizId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }

        /// <summary>Point de retrait des commandes (là où le livreur vient chercher le sac).</summary>
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }

        public bool IsOpen { get; set; }
        /// <summary>Les commandes passent directement en « Prête » (cuisine automatique sur le stock).</summary>
        public bool AutoAccept { get; set; }
        /// <summary>Suspendu par le staff : invisible pour les clients.</summary>
        public bool IsSuspended { get; set; }

        public int OrdersCount { get; set; }
        public double Revenue { get; set; }
        public int RatingSum { get; set; }
        public int RatingCount { get; set; }
        public long CreatedAt { get; set; }

        [Ignore]
        public Vector3 Position
        {
            get => new Vector3(PosX, PosY, PosZ);
            set { PosX = value.x; PosY = value.y; PosZ = value.z; }
        }

        [Ignore]
        public double Rating => RatingCount == 0 ? 0 : (double)RatingSum / RatingCount;
    }

    /// <summary>Plat proposé à la carte d'un restaurant : un item du jeu, un prix et un stock déposé par le restaurateur.</summary>
    public class UEMenuItem : ModEntity<UEMenuItem>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int RestaurantId { get; set; }
        public int ItemId { get; set; }
        public double Price { get; set; }
        public int Stock { get; set; }
        public bool IsAvailable { get; set; } = true;
    }

    /// <summary>Ligne de commande (sérialisée en JSON dans <see cref="UEOrder.LinesJson"/>).</summary>
    public class OrderLine
    {
        public int MenuItemId { get; set; }
        public int ItemId { get; set; }
        public int Quantity { get; set; }
        public double UnitPrice { get; set; }
    }

    public class UEOrder : ModEntity<UEOrder>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int ClientCharacterId { get; set; }
        public string ClientName { get; set; }
        public int RestaurantId { get; set; }
        public string RestaurantName { get; set; }
        public int CourierCharacterId { get; set; }
        public string CourierName { get; set; }
        public string CourierTransport { get; set; }

        public string LinesJson { get; set; }
        public string Note { get; set; }

        public double Subtotal { get; set; }
        public double DeliveryFee { get; set; }
        public double Tip { get; set; }

        public int Status { get; set; }

        /// <summary>Adresse de livraison (position du client au moment de la commande, modifiable).</summary>
        public float DestX { get; set; }
        public float DestY { get; set; }
        public float DestZ { get; set; }

        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long DeliveredAt { get; set; }

        public int CourierRating { get; set; }
        public int RestaurantRating { get; set; }
        public bool Rated { get; set; }

        [Ignore]
        public OrderStatus State
        {
            get => (OrderStatus)Status;
            set => Status = (int)value;
        }

        [Ignore]
        public List<OrderLine> Lines
        {
            get => string.IsNullOrEmpty(LinesJson) ? new List<OrderLine>() : JsonConvert.DeserializeObject<List<OrderLine>>(LinesJson);
            set => LinesJson = JsonConvert.SerializeObject(value);
        }

        [Ignore]
        public Vector3 Destination
        {
            get => new Vector3(DestX, DestY, DestZ);
            set { DestX = value.x; DestY = value.y; DestZ = value.z; }
        }

        [Ignore]
        public double Total => Subtotal + DeliveryFee;

        [Ignore]
        public int ItemsCount => Lines.Sum(l => l.Quantity);

        [Ignore]
        public bool IsActive => State == OrderStatus.Pending || State == OrderStatus.Preparing
                                || State == OrderStatus.Ready || State == OrderStatus.Delivering;

        /// <summary>Le client peut encore annuler tant que le livreur n'a pas récupéré le sac.</summary>
        [Ignore]
        public bool IsCancellable => State == OrderStatus.Pending || State == OrderStatus.Preparing || State == OrderStatus.Ready;

        /// <summary>Une course est visible des livreurs dès que le restaurant a accepté la commande.</summary>
        [Ignore]
        public bool IsAvailableForCourier => CourierCharacterId == 0 && (State == OrderStatus.Preparing || State == OrderStatus.Ready);
    }

    /// <summary>Profil d'un livreur indépendant : statistiques, note et niveau.</summary>
    public class UECourier : ModEntity<UECourier>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string Name { get; set; }
        public string Transport { get; set; } = "Vélo";
        public int Deliveries { get; set; }
        public double Earnings { get; set; }
        public double Tips { get; set; }
        public int RatingSum { get; set; }
        public int RatingCount { get; set; }

        [Ignore]
        public double Rating => RatingCount == 0 ? 0 : (double)RatingSum / RatingCount;
    }

    /// <summary>
    /// Argent dû à un joueur hors ligne (remboursement, gain de restaurant…) :
    /// versé sur son compte bancaire à sa prochaine connexion.
    /// </summary>
    public class UEPendingPayment : ModEntity<UEPendingPayment>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public double Amount { get; set; }
        public string Reason { get; set; }
    }
}
