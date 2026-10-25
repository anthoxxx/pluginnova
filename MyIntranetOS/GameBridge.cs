using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Life;
using Life.AreaSystem;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.PermissionSystem;
using Life.VehicleSystem;
using UnityEngine;

namespace MyIntranetOS
{
    /// <summary>
    /// Exécute du code sur le thread principal du jeu (Unity n'est pas thread-safe) et en attend le résultat
    /// depuis le thread du serveur web. La file est vidée à chaque image par une coroutine.
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int mainThreadId = -1;

        /// <summary>
        /// Démarre la file : un composant Unity propre au plugin (Update) et, en secours, une coroutine
        /// sur le gestionnaire du jeu. Les deux peuvent tourner ensemble sans risque.
        /// </summary>
        public static void Init()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            bool started = false;
            try
            {
                GameObject go = new GameObject("MyIntranetOS.MainThread");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<MainThreadPump>();
                started = true;
            }
            catch (Exception e)
            {
                Log.Warning($"composant de file non créé ({e.Message}), utilisation d'une coroutine");
            }

            MonoBehaviour host = Nova.man != null ? Nova.man : UnityEngine.Object.FindObjectOfType<LifeManager>();
            if (host != null)
            {
                host.StartCoroutine(Pump());
                started = true;
            }
            if (!started) throw new InvalidOperationException("aucun objet Unity disponible pour exécuter les actions de jeu");
        }

        internal static void Drain()
        {
            int budget = 200;
            while (budget-- > 0 && Queue.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception e) { Log.Error($"action de jeu en échec : {e}"); }
            }
        }

        private static IEnumerator Pump()
        {
            while (true)
            {
                Drain();
                yield return null;
            }
        }

        public static T Run<T>(Func<T> func, int timeoutMs = 8000)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId) return func();
            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();
            Queue.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException("Le serveur de jeu ne répond pas.");
            return tcs.Task.Result;
        }

        /// <summary>Lance une tâche asynchrone du jeu (base de données) sur le thread principal et attend son résultat.</summary>
        public static T RunAsync<T>(Func<Task<T>> func, int timeoutMs = 10000)
        {
            Task<T> task = Run(func, timeoutMs);
            if (!task.Wait(timeoutMs)) throw new TimeoutException("La base du jeu ne répond pas.");
            return task.Result;
        }

        public static void Post(Action action) => Queue.Enqueue(action);
    }

    /// <summary>Vide la file d'actions de jeu à chaque image.</summary>
    internal sealed class MainThreadPump : MonoBehaviour
    {
        private void Update() => MainThread.Drain();
    }

    // ----------------------------------------------------------------------
    //  Données échangées avec le jeu
    // ----------------------------------------------------------------------

    public struct Position
    {
        public float X, Y, Z;
        public Position(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public class CharacterRef
    {
        public int Id;
        public string FirstName;
        public string LastName;
        public string PhoneNumber;
        public string FullName => $"{FirstName} {LastName}".Trim();
    }

    public class SmsItem
    {
        public int Id;
        public string From;
        public string To;
        public string Message;
        public long Timestamp;
    }

    public class ContactItem
    {
        public int Id;
        public string Name;
        public string Number;
    }

    public class MailAccountItem
    {
        public int Id;
        public string Domain;
        public string Name;
    }

    public class MailItem
    {
        public int Id;
        public string Guid;
        public string Sender;
        public string SenderName;
        public string Recipient;
        public string RecipientName;
        public string Subject;
        public string Content;
        public long Timestamp;
        public bool Read;
        public bool IsReply;
    }

    public class VehicleItem
    {
        public int Id;
        public string Plate;
        public string Model;
        public float Fuel;
        public bool Stowed;
        public bool Owner;
        public int BizId;
        public string Color;
    }

    public class AreaItem
    {
        public uint Id;
        public string Address;
        public double Price;
        public double RentPrice;
        public bool Rentable;
        public bool Mine;
        public bool Owner;
        public bool ForSale;
        public int BizId;
    }

    public class BizItem
    {
        public int Id;
        public string Name;
        public string Description;
        public string Contact;
        public bool Recruiting;
        public List<string> Activities = new List<string>();
    }

    /// <summary>Accès au jeu utilisé par les applications. Implémentation réelle : <see cref="NovaGameBridge"/>.</summary>
    public interface IGameBridge
    {
        string ServerName { get; }
        Position? GetPlayerPosition(int characterId);
        void Notify(int characterId, string title, string message, string type);
        bool SetGps(int characterId, Position position);

        double? GetBank(int characterId);
        string TransferBank(int fromCharacterId, int toCharacterId, double amount, string reasonOut, string reasonIn, out bool delivered);
        void CreditBank(int characterId, double amount, string reason);

        CharacterRef FindCharacterByPhone(string phone);
        CharacterRef FindCharacterByName(string firstName, string lastName);

        List<SmsItem> FetchSms(int characterId);
        string SendSms(int fromCharacterId, string fromNumber, string toNumber, string message);
        List<ContactItem> FetchContacts(int characterId);
        bool CreateContact(int characterId, string name, string number);
        bool DeleteContact(int characterId, int contactId);

        List<MailAccountItem> GetMailAccounts(int characterId);
        List<MailAccountItem> GetAllMailAccounts();
        List<MailItem> FetchMails(int accountId);
        bool SendMail(MailAccountItem from, MailAccountItem to, string subject, string content);
        void DeleteMail(int accountId, int characterId, string guid);
        List<int> OnlineCharactersWithMailAccess(int accountId);

        List<VehicleItem> GetVehicles(int characterId);
        Position? GetVehiclePosition(int characterId, int vehicleId);
        List<AreaItem> GetAreas(int characterId);
        Position? GetAreaPosition(uint areaId);
        List<BizItem> GetBizs();

        /// <summary>Joueurs connectés pouvant répondre à un service (activité métier ou entreprise).</summary>
        List<int> OnlineProviders(IEnumerable<string> activities, IEnumerable<int> bizIds, bool requireOnDuty);
        bool IsProvider(int characterId, IEnumerable<string> activities, IEnumerable<int> bizIds);
    }

    /// <summary>Implémentation reliée à Nova-Life. Tous les accès au jeu passent par le thread principal.</summary>
    internal sealed class NovaGameBridge : IGameBridge
    {
        public string ServerName
        {
            get
            {
                try { return Nova.RemoveTextFormat(Nova.serverInfo?.serverName ?? ""); }
                catch { return ""; }
            }
        }

        // Recherche manuelle : les surcharges GetPlayer(...) du jeu exigent la DLL Steamworks à la compilation
        private static Player Online(int characterId) =>
            Nova.server.Players.FirstOrDefault(p => p?.character != null && p.character.Id == characterId);

        private static Player OnlineByPhone(string phone) =>
            Nova.server.Players.FirstOrDefault(p => p?.character != null && p.character.PhoneNumber == phone);

        public Position? GetPlayerPosition(int characterId) => MainThread.Run<Position?>(() =>
        {
            Player p = Online(characterId);
            if (p?.setup == null) return null;
            Vector3 v = p.setup.transform.position;
            return new Position(v.x, v.y, v.z);
        });

        public void Notify(int characterId, string title, string message, string type)
        {
            MainThread.Post(() =>
            {
                Player p = Online(characterId);
                if (p == null) return;
                NotificationManager.Type t = type == "success" ? NotificationManager.Type.Success
                    : type == "warning" ? NotificationManager.Type.Warning
                    : type == "error" ? NotificationManager.Type.Error
                    : NotificationManager.Type.Info;
                p.Notify(title, message, t, 6f);
            });
        }

        public bool SetGps(int characterId, Position position) => MainThread.Run(() =>
        {
            Player p = Online(characterId);
            if (p == null) return false;
            NCheckpoint checkpoint = null;
            checkpoint = new NCheckpoint(p.netId, new Vector3(position.X, position.Y, position.Z), _ =>
            {
                try { p.DestroyCheckpoint(checkpoint); } catch { }
            });
            p.CreateCheckpoint(checkpoint);
            return true;
        });

        public double? GetBank(int characterId) => MainThread.Run<double?>(() => Online(characterId)?.character?.Bank);

        public string TransferBank(int fromCharacterId, int toCharacterId, double amount, string reasonOut, string reasonIn, out bool delivered)
        {
            bool done = false;
            string error = MainThread.Run(() =>
            {
                Player sender = Online(fromCharacterId);
                if (sender?.character == null) return "Vous devez être connecté au serveur.";
                if (sender.character.Bank < amount) return "Solde insuffisant.";
                sender.AddBankMoney(-amount, reasonOut);
                Player recipient = Online(toCharacterId);
                if (recipient?.character != null)
                {
                    recipient.AddBankMoney(amount, reasonIn);
                    done = true;
                }
                return null;
            });
            delivered = done;
            return error;
        }

        public void CreditBank(int characterId, double amount, string reason)
        {
            MainThread.Run(() =>
            {
                Online(characterId)?.AddBankMoney(amount, reason);
                return true;
            });
        }

        private static CharacterRef Ref(Characters c) => c == null || c.Id == 0 ? null : new CharacterRef
        {
            Id = c.Id, FirstName = c.Firstname, LastName = c.Lastname, PhoneNumber = c.PhoneNumber
        };

        public CharacterRef FindCharacterByPhone(string phone) => Ref(MainThread.RunAsync(() => LifeDB.FetchCharacter(phone)));

        public CharacterRef FindCharacterByName(string firstName, string lastName) =>
            Ref(MainThread.RunAsync(() => LifeDB.FetchCharacter(firstName, lastName)));

        public List<SmsItem> FetchSms(int characterId)
        {
            SMSList list = MainThread.RunAsync(() => LifeDB.FetchSMS(characterId));
            return (list.sms ?? new MessageSMS[0]).Select(s => new SmsItem
            {
                Id = s.id, From = s.numberEmitter, To = s.numberReceiver, Message = s.message, Timestamp = s.timestamp
            }).ToList();
        }

        /// <summary>Même logique que le téléphone du jeu : une copie pour chaque personnage, puis mise à jour en direct.</summary>
        public string SendSms(int fromCharacterId, string fromNumber, string toNumber, string message)
        {
            Characters recipient = MainThread.RunAsync(() => LifeDB.FetchCharacter(toNumber));
            if (recipient == null || recipient.Id == 0) return "Ce numéro n'est attribué à personne.";
            long now = Nova.UnixTimeNow();
            MainThread.RunAsync(() => LifeDB.SendSMS(recipient.Id, fromNumber, toNumber, now, message));
            MainThread.RunAsync(() => LifeDB.SendSMS(fromCharacterId, fromNumber, toNumber, now, message));
            MainThread.Post(() =>
            {
                try { Online(fromCharacterId)?.setup?.TargetUpdateSMS(); } catch { }
                Player target = OnlineByPhone(toNumber);
                if (target != null)
                {
                    try { target.setup?.TargetUpdateSMS(); } catch { }
                }
            });
            return null;
        }

        public List<ContactItem> FetchContacts(int characterId)
        {
            ContactsList list = MainThread.RunAsync(() => LifeDB.FetchContacts(characterId));
            return (list.contacts ?? new Contact[0]).Select(c => new ContactItem { Id = c.id, Name = c.name, Number = c.number }).ToList();
        }

        public bool CreateContact(int characterId, string name, string number) =>
            MainThread.RunAsync(() => LifeDB.CreateContact(characterId, number, name)) != null;

        public bool DeleteContact(int characterId, int contactId) =>
            MainThread.RunAsync(() => LifeDB.DeleteContact(characterId, contactId));

        private static MailAccountItem Item(MailAccount a) => new MailAccountItem { Id = a.id, Domain = a.domain, Name = a.name };

        public List<MailAccountItem> GetMailAccounts(int characterId) => MainThread.Run(() =>
            (Nova.server.mails.GetMailAccounts(characterId).accounts ?? new MailAccount[0]).Select(Item).ToList());

        public List<MailAccountItem> GetAllMailAccounts()
        {
            List<MailAccounts> all = MainThread.RunAsync(() => LifeDB.FetchMailAccounts());
            return (all ?? new List<MailAccounts>()).Select(a => new MailAccountItem { Id = a.Id, Domain = a.Domain, Name = a.Name }).ToList();
        }

        public List<MailItem> FetchMails(int accountId)
        {
            MailsJson json = MainThread.RunAsync(() => LifeDB.FetchMails(accountId));
            return (json.mails ?? new Mail[0]).Select(m => new MailItem
            {
                Id = m.id, Guid = m.guid, Sender = m.sender, SenderName = m.realSender, Recipient = m.recipient,
                RecipientName = m.realRecipient, Subject = m.subject, Content = m.content, Read = m.readed
            }).ToList();
        }

        public bool SendMail(MailAccountItem from, MailAccountItem to, string subject, string content) =>
            MainThread.RunAsync(() => Nova.SendMail(from.Domain, to.Domain, from.Name, to.Name, subject, content));

        public void DeleteMail(int accountId, int characterId, string guid)
        {
            MainThread.Run(() =>
            {
                Player p = Online(characterId);
                if (p != null) Nova.server.mails.DeleteMail(accountId, p, guid);
                return true;
            });
        }

        public List<int> OnlineCharactersWithMailAccess(int accountId) => MainThread.Run(() =>
        {
            MailAccount account = Nova.server.mails.GetMailAccount(accountId);
            if (account.permissions == null) return new List<int>();
            return Nova.server.GetAllInGamePlayers()
                .Where(p => p.character != null && account.permissions.HasPermission(p.character.Id))
                .Select(p => p.character.Id).ToList();
        });

        public List<VehicleItem> GetVehicles(int characterId) => MainThread.Run(() =>
        {
            List<VehicleItem> result = new List<VehicleItem>();
            foreach (LifeVehicle v in Nova.v.vehicles ?? new List<LifeVehicle>())
            {
                if (v?.permissions == null || !v.permissions.HasPermission(characterId)) continue;
                string model = null;
                try
                {
                    string[] names = Nova.v.vehiclesModelName;
                    if (names != null && v.modelId >= 0 && v.modelId < names.Length) model = names[v.modelId];
                }
                catch { }
                result.Add(new VehicleItem
                {
                    Id = v.vehicleId,
                    Plate = v.plate,
                    Model = model ?? $"Modèle {v.modelId}",
                    Fuel = v.fuel,
                    Stowed = v.isStowed,
                    Owner = v.permissions.HasOwnerPermission(characterId),
                    BizId = v.bizId,
                    Color = v.color
                });
            }
            return result;
        });

        public Position? GetVehiclePosition(int characterId, int vehicleId) => MainThread.Run<Position?>(() =>
        {
            LifeVehicle v = Nova.v.GetVehicle(vehicleId);
            if (v?.permissions == null || !v.permissions.HasPermission(characterId) || v.isStowed) return null;
            if (v.instance != null)
            {
                Vector3 p = v.instance.transform.position;
                return new Position(p.x, p.y, p.z);
            }
            return new Position(v.x, v.y, v.z);
        });

        public List<AreaItem> GetAreas(int characterId) => MainThread.Run(() =>
        {
            List<AreaItem> result = new List<AreaItem>();
            foreach (LifeArea a in Nova.a.areas ?? new LifeArea[0])
            {
                if (a == null) continue;
                bool hasOwner = a.permissions != null && a.permissions.HasOwner();
                bool mine = a.permissions != null && a.permissions.HasPermission(characterId);
                bool forSale = !hasOwner && (a.price > 0 || a.isRentable);
                if (!mine && !forSale) continue;
                result.Add(new AreaItem
                {
                    Id = a.areaId,
                    Address = string.IsNullOrWhiteSpace(a.address) ? $"Terrain n°{a.areaId}" : a.address,
                    Price = a.price,
                    RentPrice = a.rentPrice,
                    Rentable = a.isRentable,
                    Mine = mine,
                    Owner = a.permissions != null && a.permissions.HasOwnerPermission(characterId),
                    ForSale = forSale,
                    BizId = a.bizId
                });
            }
            return result;
        });

        public Position? GetAreaPosition(uint areaId) => MainThread.Run<Position?>(() =>
        {
            LifeArea a = Nova.a.GetAreaById(areaId);
            if (a?.instance == null) return null;
            Vector3 s = a.instance.spawn;
            return new Position(s.x, s.y, s.z);
        });

        public List<BizItem> GetBizs() => MainThread.Run(() => Nova.biz.bizs.Select(b => new BizItem
        {
            Id = b.Id,
            Name = Nova.RemoveTextFormat(b.BizName ?? ""),
            Description = b.Description,
            Contact = b.Contact,
            Recruiting = b.IsRecruiting,
            Activities = SafeActivities(b.Id)
        }).ToList());

        private static List<string> SafeActivities(int bizId)
        {
            try { return Nova.biz.GetBizActivities(bizId).Select(a => a.ToString()).ToList(); }
            catch { return new List<string>(); }
        }

        private static bool Matches(Player p, HashSet<string> activities, HashSet<int> bizIds)
        {
            if (p?.character == null || p.character.BizId == 0) return false;
            if (bizIds.Contains(p.character.BizId)) return true;
            return activities.Count > 0 && p.GetActivities().Any(a => activities.Contains(a.ToString()));
        }

        public List<int> OnlineProviders(IEnumerable<string> activities, IEnumerable<int> bizIds, bool requireOnDuty)
        {
            HashSet<string> acts = new HashSet<string>(activities ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<int> ids = new HashSet<int>(bizIds ?? Enumerable.Empty<int>());
            return MainThread.Run(() => Nova.server.GetAllInGamePlayers()
                .Where(p => Matches(p, acts, ids) && (!requireOnDuty || p.serviceMetier))
                .Select(p => p.character.Id).ToList());
        }

        public bool IsProvider(int characterId, IEnumerable<string> activities, IEnumerable<int> bizIds)
        {
            HashSet<string> acts = new HashSet<string>(activities ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<int> ids = new HashSet<int>(bizIds ?? Enumerable.Empty<int>());
            return MainThread.Run(() => Matches(Online(characterId), acts, ids));
        }
    }
}
