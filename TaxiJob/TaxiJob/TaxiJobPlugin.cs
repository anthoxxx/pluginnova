using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AAMenu;
using Life;
using Life.BizSystem;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Life.VehicleSystem;
using Mirror;
using ModKit;
using ModKit.Helper;
using ModKit.Helper.JobHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using UnityEngine;

namespace TaxiJob;

public class TaxiJobPlugin : ModKit
{
	private const string Title = "Taxi";

	public const string Author = "Matheo Mercier";

	private static readonly Type TaxiActivity = (Type)6;

	private TaxiSettings _settings = new TaxiSettings();

	private List<TaxiPoint> _points = new List<TaxiPoint>();

	private readonly HashSet<uint> _onDuty = new HashSet<uint>();

	private readonly Dictionary<uint, Ride> _rides = new Dictionary<uint, Ride>();

	private readonly Dictionary<uint, List<DateTime>> _rideHistory = new Dictionary<uint, List<DateTime>>();

	private readonly List<TaxiCall> _calls = new List<TaxiCall>();

	private readonly Random _rng = new Random();

	private int _nextCallId = 1;

	private static readonly List<SettingField> SettingFields = new List<SettingField>
	{
		SettingField.Int("ID de l'entreprise taxi (0 = ouvert à tous, comme un farm)", (TaxiSettings s) => s.BizId, delegate(TaxiSettings s, int v)
		{
			s.BizId = v;
		}, 0, 100000),
		SettingField.Bool("Prise de service obligatoire", (TaxiSettings s) => s.RequireService, delegate(TaxiSettings s, bool v)
		{
			s.RequireService = v;
		}),
		SettingField.Bool("Être au volant pour lancer une course", (TaxiSettings s) => s.RequireVehicle, delegate(TaxiSettings s, bool v)
		{
			s.RequireVehicle = v;
		}),
		SettingField.Bool("Véhicule de l'entreprise taxi obligatoire", (TaxiSettings s) => s.RequireBizVehicle, delegate(TaxiSettings s, bool v)
		{
			s.RequireBizVehicle = v;
		}),
		SettingField.Int("Rayon de validation d'un point (m)", (TaxiSettings s) => s.TriggerRadius, delegate(TaxiSettings s, int v)
		{
			s.TriggerRadius = v;
		}, 2, 50),
		SettingField.Int("Prise en charge (€)", (TaxiSettings s) => s.BasePay, delegate(TaxiSettings s, int v)
		{
			s.BasePay = v;
		}, 0, 1000000),
		SettingField.Int("Prix au kilomètre (€)", (TaxiSettings s) => s.PayPerKm, delegate(TaxiSettings s, int v)
		{
			s.PayPerKm = v;
		}, 0, 1000000),
		SettingField.Int("Paie minimum par course (€, 0 = aucun)", (TaxiSettings s) => s.MinPay, delegate(TaxiSettings s, int v)
		{
			s.MinPay = v;
		}, 0, 1000000),
		SettingField.Int("Paie maximum par course (€, 0 = aucun)", (TaxiSettings s) => s.MaxPay, delegate(TaxiSettings s, int v)
		{
			s.MaxPay = v;
		}, 0, 1000000),
		SettingField.Int("Pourboire si course rapide (%)", (TaxiSettings s) => s.TipPercent, delegate(TaxiSettings s, int v)
		{
			s.TipPercent = v;
		}, 0, 1000),
		SettingField.Int("Vitesse de référence pour le pourboire (km/h)", (TaxiSettings s) => s.ReferenceSpeedKmh, delegate(TaxiSettings s, int v)
		{
			s.ReferenceSpeedKmh = v;
		}, 1, 500),
		SettingField.Int("Distance minimum d'une course (m)", (TaxiSettings s) => s.MinRideDistance, delegate(TaxiSettings s, int v)
		{
			s.MinRideDistance = v;
		}, 0, 100000),
		SettingField.Bool("Enchaîner les courses automatiquement", (TaxiSettings s) => s.AutoChain, delegate(TaxiSettings s, bool v)
		{
			s.AutoChain = v;
		}),
		SettingField.Int("Courses max par heure (0 = illimité)", (TaxiSettings s) => s.MaxRidesPerHour, delegate(TaxiSettings s, int v)
		{
			s.MaxRidesPerHour = v;
		}, 0, 1000),
		SettingField.Bool("Appels de joueurs (/appeltaxi)", (TaxiSettings s) => s.AllowPlayerCalls, delegate(TaxiSettings s, bool v)
		{
			s.AllowPlayerCalls = v;
		}),
		SettingField.Int("Expiration d'un appel (min, 0 = jamais)", (TaxiSettings s) => s.CallExpireMinutes, delegate(TaxiSettings s, int v)
		{
			s.CallExpireMinutes = v;
		}, 0, 1440),
		SettingField.Int("Prime au chauffeur par appel joueur (€)", (TaxiSettings s) => s.CallBonus, delegate(TaxiSettings s, int v)
		{
			s.CallBonus = v;
		}, 0, 1000000)
	};

	private const string Green = "#2ecc71";

	private const string Red = "#e74c3c";

	private const string Gold = "#f1c40f";

	public static TaxiJobPlugin Instance { get; private set; }

	public TaxiJobPlugin(IGameAPI api)
		: base(api)
	{
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Expected O, but got Unknown
		base.PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "2.1.0", "Matheo Mercier");
	}

	public override void OnPluginInit()
	{
		((Plugin)this).OnPluginInit();
		Instance = this;
		((ModKit)this).Orm.RegisterTable<TaxiSettings>();
		((ModKit)this).Orm.RegisterTable<TaxiPoint>();
		((ModKit)this).Orm.RegisterTable<TaxiDriverStats>();
		((ModKit)this).Orm.RegisterTable<TaxiStation>();
		TaxiStation taxiStation = new TaxiStation(isCreated: false)
		{
			Context = (ModKit)(object)this
		};
		((ModKit)this).PointHelper.AddPattern("TaxiStation", (PatternData)(object)taxiStation);
		if (AAMenu.menu != null)
		{
			AAMenu.menu.AddBuilder(base.PluginInformations, "Taxi", (PatternData)(object)taxiStation, (ModKit)(object)this);
			Menu.AddAdminPluginTabLine(base.PluginInformations, 1, "Taxi - Configuration du job", (Action<UIPanel>)delegate(UIPanel ui)
			{
				OpenAdminMenu(((ModKit)this).PanelHelper.ReturnPlayerFromPanel(ui));
			});
			InsertBizMenu();
		}
		else
		{
			Logger.LogWarning(base.PluginInformations.SourceName, "AAMenu introuvable : utilisez /taxi et /taxiadmin.");
		}
		RegisterCommands();
		LifeServer server = Nova.server;
		server.OnMinutePassedEvent = (Action)Delegate.Combine(server.OnMinutePassedEvent, new Action(OnMinutePassed));
		TaxiTicker.Start(this);
		LoadDataAsync();
		Logger.LogSuccess(base.PluginInformations.SourceName + " v" + base.PluginInformations.Version + " by Matheo Mercier", "initialisé");
	}

	public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
	{
		((ModKit)this).OnPlayerSpawnCharacter(player, conn, character);
		ResetPlayer(player.netId);
		((ModKit)this).PointHelper.InitAllNPoint(player);
	}

	private async Task LoadDataAsync()
	{
		_ = 3;
		try
		{
			_settings = (await ModEntity<TaxiSettings>.QueryAll()).FirstOrDefault();
			if (_settings == null)
			{
				_settings = new TaxiSettings();
				await ImportOldConfig();
				await ((ModEntity<TaxiSettings>)_settings).Save();
			}
			await ReloadPoints();
			Logger.LogSuccess(base.PluginInformations.SourceName, $"{_points.Count} point(s) de course chargé(s)");
		}
		catch (Exception ex)
		{
			Logger.LogWarning(base.PluginInformations.SourceName, "Erreur au chargement des données : " + ex.Message);
		}
	}

	private async Task ReloadPoints()
	{
		_points = await ModEntity<TaxiPoint>.QueryAll();
	}

	private async Task ImportOldConfig()
	{
		string path = Path.Combine(((Plugin)this).pluginsPath, "TaxiJob", "config.json");
		if (!File.Exists(path))
		{
			return;
		}
		try
		{
			OldConfig old = JsonConvert.DeserializeObject<OldConfig>(File.ReadAllText(path));
			if (old == null)
			{
				return;
			}
			_settings.BizId = old.BizId;
			_settings.MinPay = old.MinPay;
			_settings.MaxPay = old.MaxPay;
			int index = 1;
			if (old.StartPoint != null && (old.StartPoint.X != 0f || old.StartPoint.Y != 0f || old.StartPoint.Z != 0f))
			{
				await ((ModEntity<TaxiPoint>)new TaxiPoint
				{
					Name = "Départ",
					X = old.StartPoint.X,
					Y = old.StartPoint.Y,
					Z = old.StartPoint.Z,
					IsPickup = true,
					IsDropoff = false,
					Enabled = true
				}).Save();
			}
			foreach (OldPos item in old.Points ?? new List<OldPos>())
			{
				await ((ModEntity<TaxiPoint>)new TaxiPoint
				{
					Name = $"Arrêt {index++}",
					X = item.X,
					Y = item.Y,
					Z = item.Z,
					IsPickup = true,
					IsDropoff = true,
					Enabled = true
				}).Save();
			}
			File.Move(path, path + ".importe");
			Logger.LogSuccess(base.PluginInformations.SourceName, "Ancienne config jobtaxibymatheo importée");
		}
		catch (Exception ex)
		{
			Logger.LogWarning(base.PluginInformations.SourceName, "Import de l'ancienne config impossible : " + ex.Message);
		}
	}

	private async Task<bool> SaveSettings(Player player)
	{
		if (await ((ModEntity<TaxiSettings>)_settings).Save())
		{
			return true;
		}
		Notify(player, "Erreur lors de l'enregistrement des paramètres.", (Type)3);
		return false;
	}

	private void InsertBizMenu()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		List<Type> list = new List<Type> { TaxiActivity };
		Menu.AddBizTabLine(base.PluginInformations, list, (CustomActivity)null, "Taxi - Menu chauffeur", (Action<UIPanel>)delegate(UIPanel ui)
		{
			OpenDriverMenu(GetPlayer(ui.playerId));
		});
		Menu.AddBizTabLine(base.PluginInformations, list, (CustomActivity)null, "Taxi - Prendre / quitter le service", (Action<UIPanel>)delegate(UIPanel ui)
		{
			ToggleService(GetPlayer(ui.playerId));
		});
		Menu.AddBizTabLine(base.PluginInformations, list, (CustomActivity)null, "Taxi - Commencer une course", (Action<UIPanel>)delegate(UIPanel ui)
		{
			StartSoloRide(GetPlayer(ui.playerId), verbose: true);
		});
	}

	private void RegisterCommands()
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		new SChatCommand("/taxi", "Menu chauffeur de taxi", "/taxi [service|course|annuler]", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			switch ((args.Length != 0) ? args[0].ToLowerInvariant() : "")
			{
			case "service":
				ToggleService(player);
				break;
			case "course":
				StartSoloRide(player, verbose: true);
				break;
			case "annuler":
				CancelRide(player, verbose: true);
				break;
			default:
				OpenDriverMenu(player);
				break;
			}
		}).Register();
		new SChatCommand("/appeltaxi", "Appeler un taxi à votre position", "/appeltaxi [message|annuler]", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			if (args.Length == 1 && args[0].ToLowerInvariant() == "annuler")
			{
				CancelCall(player);
			}
			else
			{
				CallTaxi(player, string.Join(" ", args));
			}
		}).Register();
		new SChatCommand("/taxiadmin", "Configurer le job taxi (staff)", "/taxiadmin", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			if (!player.IsAdmin || !player.serviceAdmin)
			{
				Notify(player, "Vous devez être staff et en service admin.", (Type)3);
			}
			else
			{
				OpenAdminMenu(player);
			}
		}).Register();
	}

	private bool IsTaxi(Player p)
	{
		if (p == null || p.character == null)
		{
			return false;
		}
		if (_settings.BizId > 0)
		{
			return p.character.BizId == _settings.BizId;
		}
		return true;
	}

	private bool IsOnDuty(Player p)
	{
		if (_settings.RequireService)
		{
			return _onDuty.Contains(p.netId);
		}
		return true;
	}

	private bool CanWork(Player p)
	{
		if (p == null)
		{
			return false;
		}
		if (!IsTaxi(p))
		{
			Notify(p, "Vous n'êtes pas employé de la compagnie de taxi.", (Type)3);
			return false;
		}
		if (!IsOnDuty(p))
		{
			Notify(p, "Prenez d'abord votre service.", (Type)2);
			return false;
		}
		return true;
	}

	private static Vehicle DrivenVehicle(Player p)
	{
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)p?.setup == (Object)null)
		{
			return null;
		}
		uint vehicleId = p.GetVehicleId();
		if (vehicleId == 0 || !NetworkServer.spawned.TryGetValue(vehicleId, out var value) || (Object)(object)value == (Object)null)
		{
			return null;
		}
		Vehicle component = ((Component)value).GetComponent<Vehicle>();
		if ((Object)(object)component == (Object)null || component.netSeats == null || ((SyncList<NetVehicleSeat>)(object)component.netSeats).Count == 0)
		{
			return null;
		}
		if (((SyncList<NetVehicleSeat>)(object)component.netSeats)[0].passengerId != ((NetworkBehaviour)p.setup).netId)
		{
			return null;
		}
		return component;
	}

	private static int DrivenModelId(Player p)
	{
		Vehicle val = DrivenVehicle(p);
		if ((Object)(object)val == (Object)null)
		{
			return -1;
		}
		return Nova.v.GetVehicle(val.VehicleDbId)?.modelId ?? (-2);
	}

	private string VehicleProblem(Player p)
	{
		List<int> allowedModelIds = _settings.AllowedModelIds;
		bool flag = _settings.RequireBizVehicle && _settings.BizId > 0;
		if (!_settings.RequireVehicle && allowedModelIds.Count == 0 && !flag)
		{
			return null;
		}
		Vehicle val = DrivenVehicle(p);
		if ((Object)(object)val == (Object)null)
		{
			return "Vous devez être au volant d'un véhicule.";
		}
		if (flag && val.bizId != _settings.BizId)
		{
			return "Ce véhicule n'appartient pas à la compagnie de taxi.";
		}
		if (allowedModelIds.Count > 0 && !allowedModelIds.Contains(DrivenModelId(p)))
		{
			return "Ce véhicule n'est pas un taxi autorisé.";
		}
		return null;
	}

	private bool CheckVehicle(Player p)
	{
		string text = VehicleProblem(p);
		if (text == null)
		{
			return true;
		}
		Notify(p, text, (Type)2);
		return false;
	}

	public void OpenDriverMenu(Player player)
	{
		if (player == null)
		{
			return;
		}
		if (!IsTaxi(player))
		{
			Notify(player, "Vous n'êtes pas employé de la compagnie de taxi. Pour appeler un taxi : /appeltaxi", (Type)0);
			return;
		}
		_rides.TryGetValue(player.netId, out var value);
		int count = PendingCalls().Count;
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Menu chauffeur", (PanelType)2, player, (Action)delegate
		{
			OpenDriverMenu(player);
		});
		panel.TextLines.Add(Col("By Matheo Mercier", "#f1c40f"));
		if (_settings.RequireService)
		{
			bool flag = _onDuty.Contains(player.netId);
			((UIPanel)panel).AddTabLine(flag ? ("Service : " + Col("EN SERVICE", "#2ecc71") + " (quitter)") : ("Service : " + Col("HORS SERVICE", "#e74c3c") + " (prendre)"), (Action<UIPanel>)delegate
			{
				ToggleService(player);
				panel.Refresh();
			});
		}
		if (value == null)
		{
			((UIPanel)panel).AddTabLine("Commencer une course (client PNJ)", (Action<UIPanel>)delegate
			{
				StartSoloRide(player, verbose: true);
			});
			((UIPanel)panel).AddTabLine($"Appels clients en attente ({count})", (Action<UIPanel>)delegate
			{
				CallsMenu(player);
			});
		}
		else
		{
			((UIPanel)panel).AddTabLine("Course en cours : " + Col(RideDescription(value), "#f1c40f"), (Action<UIPanel>)delegate
			{
			});
			((UIPanel)panel).AddTabLine(Col("Annuler la course en cours", "#e74c3c"), (Action<UIPanel>)delegate
			{
				CancelRide(player, verbose: true);
				panel.Refresh();
			});
		}
		((UIPanel)panel).AddTabLine("Enchaîner les courses automatiquement : " + YesNo(_settings.AutoChain), (Action<UIPanel>)delegate
		{
		});
		((UIPanel)panel).AddTabLine("Mes statistiques", (Action<UIPanel>)async delegate
		{
			await MyStatsMenu(player);
		});
		((UIPanel)panel).AddTabLine("Classement des chauffeurs", (Action<UIPanel>)async delegate
		{
			await LeaderboardMenu(player, staff: false);
		});
		panel.NextButton("Valider", (Action)delegate
		{
			((UIPanel)panel).SelectTab();
		});
		panel.CloseButton();
		panel.Display();
	}

	private void ToggleService(Player p)
	{
		if (p != null)
		{
			if (!IsTaxi(p))
			{
				Notify(p, "Vous n'êtes pas employé de la compagnie de taxi.", (Type)3);
			}
			else if (!_settings.RequireService)
			{
				Notify(p, "La prise de service n'est pas nécessaire sur ce serveur.", (Type)0);
			}
			else if (_onDuty.Remove(p.netId))
			{
				CancelRide(p, verbose: false);
				Notify(p, "Fin de service. Bonne journée !", (Type)0);
			}
			else
			{
				_onDuty.Add(p.netId);
				int count = PendingCalls().Count;
				Notify(p, (count > 0) ? $"Service pris. {count} appel(s) client en attente (/taxi)." : "Service pris. Montez dans votre taxi !", (Type)1);
			}
		}
	}

	private void CallsMenu(Player player)
	{
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		List<TaxiCall> list = PendingCalls();
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Appels clients", (PanelType)2, player, (Action)delegate
		{
			CallsMenu(player);
		});
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucun appel en attente", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (TaxiCall call in list)
		{
			int num = (int)(DateTime.Now - call.CreatedAt).TotalMinutes;
			int num2 = (int)Vector3.Distance(Position(player), call.Position);
			string text = (string.IsNullOrWhiteSpace(call.Message) ? "" : (" « " + call.Message + " »"));
			((UIPanel)panel).AddTabLine($"{call.CallerName} - {num2} m - il y a {num} min{text}", (Action<UIPanel>)delegate
			{
				AcceptCall(player, call);
			});
		}
		if (list.Count > 0)
		{
			panel.NextButton("Accepter", (Action)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void StartSoloRide(Player p, bool verbose)
	{
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		if (!CanWork(p))
		{
			return;
		}
		if (_rides.ContainsKey(p.netId))
		{
			if (verbose)
			{
				Notify(p, "Vous avez déjà une course en cours (/taxi annuler pour l'abandonner).", (Type)2);
			}
		}
		else if (_settings.MaxRidesPerHour > 0 && RidesLastHour(p.netId) >= _settings.MaxRidesPerHour)
		{
			Notify(p, $"Vous avez atteint la limite de {_settings.MaxRidesPerHour} courses par heure. Faites une pause !", (Type)2);
		}
		else
		{
			if (!CheckVehicle(p))
			{
				return;
			}
			List<TaxiPoint> pickups = _points.Where((TaxiPoint x) => x.Enabled && x.IsPickup).ToList();
			if (pickups.Count == 0 || !_points.Any((TaxiPoint x) => x.Enabled && x.IsDropoff && pickups.Any((TaxiPoint y) => y.Id != x.Id)))
			{
				Notify(p, "Aucune course disponible : le staff doit configurer des points de course.", (Type)3);
				return;
			}
			Vector3 here = Position(p);
			List<TaxiPoint> list = pickups.Where((TaxiPoint x) => Vector3.Distance(here, x.Position) > 50f).ToList();
			TaxiPoint taxiPoint = Pick((list.Count > 0) ? list : pickups);
			Ride ride = new Ride
			{
				DriverId = p.netId,
				Stage = RideStage.ToPickup,
				Pickup = taxiPoint,
				StageStart = DateTime.Now
			};
			_rides[p.netId] = ride;
			SetRideCheckpoint(p, ride, taxiPoint.Position, OnPickupReached);
			int num = (int)Vector3.Distance(here, taxiPoint.Position);
			Notify(p, $"Nouveau client : {taxiPoint.Name} ({num} m). Rejoignez le point orange en voiture.", (Type)0);
		}
	}

	private void OnPickupReached(Player p, Ride ride)
	{
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		List<TaxiPoint> list = _points.Where((TaxiPoint x) => x.Enabled && x.IsDropoff && x.Id != ride.Pickup.Id).ToList();
		if (list.Count == 0)
		{
			_rides.Remove(p.netId);
			Notify(p, "Plus aucune destination disponible, course annulée.", (Type)3);
			return;
		}
		Vector3 from = ride.Pickup.Position;
		List<TaxiPoint> list2 = list.Where((TaxiPoint x) => Vector3.Distance(from, x.Position) >= (float)_settings.MinRideDistance).ToList();
		TaxiPoint taxiPoint = ((list2.Count > 0) ? Pick(list2) : list.OrderByDescending((TaxiPoint x) => Vector3.Distance(from, x.Position)).First());
		ride.Dropoff = taxiPoint;
		ride.Distance = Vector3.Distance(from, taxiPoint.Position);
		ride.Fare = ComputeFare(ride.Distance);
		ride.TimeLimit = ComputeTimeLimit(ride.Distance);
		ride.Stage = RideStage.ToDropoff;
		ride.StageStart = DateTime.Now;
		SetRideCheckpoint(p, ride, taxiPoint.Position, OnDropoffReached);
		Notify(p, $"Client à bord ! Direction {taxiPoint.Name} ({(int)ride.Distance} m). Tarif : {ride.Fare}€. " + "Pourboire si vous arrivez en moins de " + FormatDuration(ride.TimeLimit) + ".", (Type)1);
	}

	private void OnDropoffReached(Player p, Ride ride)
	{
		_rides.Remove(p.netId);
		double totalSeconds = (DateTime.Now - ride.StageStart).TotalSeconds;
		int num = ((totalSeconds <= ride.TimeLimit) ? (ride.Fare * _settings.TipPercent / 100) : 0);
		int num2 = ride.Fare + num;
		p.AddMoney((double)num2, "Course taxi");
		AddRideToHistory(p.netId);
		AddStats(p, num2, ride.Distance);
		string arg = ((num > 0) ? $"{ride.Fare}€ + {num}€ de pourboire" : $"{ride.Fare}€ (pas de pourboire, trop lent)");
		Notify(p, $"Course terminée en {FormatDuration(totalSeconds)} : +{num2}€ ({arg}).", (Type)1);
		if (_settings.AutoChain)
		{
			StartSoloRide(p, verbose: false);
		}
		else
		{
			Notify(p, "Nouvelle course : /taxi course ou le menu taxi.", (Type)0);
		}
	}

	private int ComputeFare(float meters)
	{
		double num = (double)_settings.BasePay + (double)((float)_settings.PayPerKm * meters) / 1000.0;
		if (_settings.MinPay > 0)
		{
			num = Math.Max(num, _settings.MinPay);
		}
		if (_settings.MaxPay > 0)
		{
			num = Math.Min(num, _settings.MaxPay);
		}
		return (int)Math.Round(num);
	}

	private double ComputeTimeLimit(float meters)
	{
		double num = (double)Math.Max(1, _settings.ReferenceSpeedKmh) / 3.6;
		return (double)meters / num + 30.0;
	}

	private void CallTaxi(Player caller, string message)
	{
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		if (!_settings.AllowPlayerCalls)
		{
			Notify(caller, "Le service d'appel de taxi est désactivé.", (Type)3);
			return;
		}
		if (_calls.Any((TaxiCall c) => c.CallerId == caller.netId))
		{
			Notify(caller, "Vous avez déjà un appel en cours (/appeltaxi annuler).", (Type)2);
			return;
		}
		List<Player> list = AvailableDrivers();
		if (list.Count == 0)
		{
			Notify(caller, "Aucun chauffeur de taxi n'est en service pour le moment.", (Type)2);
			return;
		}
		TaxiCall taxiCall = new TaxiCall
		{
			Id = _nextCallId++,
			CallerId = caller.netId,
			CallerName = caller.FullName,
			Position = Position(caller),
			Message = message?.Trim(),
			CreatedAt = DateTime.Now
		};
		_calls.Add(taxiCall);
		Notify(caller, $"Appel envoyé à {list.Count} chauffeur(s). Restez sur place.", (Type)1);
		string text = (string.IsNullOrWhiteSpace(taxiCall.Message) ? "" : (" : « " + taxiCall.Message + " »"));
		foreach (Player item in list)
		{
			Notify(item, "Appel client de " + taxiCall.CallerName + text + ". Acceptez-le avec /taxi.", (Type)0);
		}
	}

	private void CancelCall(Player caller)
	{
		TaxiCall taxiCall = _calls.FirstOrDefault((TaxiCall c) => c.CallerId == caller.netId);
		if (taxiCall == null)
		{
			Notify(caller, "Vous n'avez aucun appel en cours.", (Type)0);
			return;
		}
		RemoveCall(taxiCall, "Le client a annulé son appel.");
		Notify(caller, "Appel annulé.", (Type)0);
	}

	private void AcceptCall(Player driver, TaxiCall call)
	{
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		if (CanWork(driver))
		{
			if (_rides.ContainsKey(driver.netId))
			{
				Notify(driver, "Terminez ou annulez d'abord votre course en cours.", (Type)2);
			}
			else if (!_calls.Contains(call) || call.TakenBy != 0)
			{
				Notify(driver, "Cet appel a déjà été pris ou annulé.", (Type)2);
			}
			else if (CheckVehicle(driver))
			{
				call.TakenBy = driver.netId;
				Ride ride = new Ride
				{
					DriverId = driver.netId,
					Stage = RideStage.ToPickup,
					Call = call,
					StageStart = DateTime.Now
				};
				_rides[driver.netId] = ride;
				SetRideCheckpoint(driver, ride, call.Position, OnCallerReached);
				Notify(driver, "Appel accepté : rejoignez " + call.CallerName + " (point orange).", (Type)1);
				Notify(GetPlayer(call.CallerId), "Le taxi de " + driver.FullName + " arrive !", (Type)1);
			}
		}
	}

	private void OnCallerReached(Player driver, Ride ride)
	{
		_rides.Remove(driver.netId);
		_calls.Remove(ride.Call);
		Notify(driver, "Vous êtes arrivé. " + ride.Call.CallerName + " vous attend : convenez du prix de la course.", (Type)1);
		Notify(GetPlayer(ride.Call.CallerId), "Votre taxi est arrivé !", (Type)1);
		if (_settings.CallBonus > 0)
		{
			driver.AddMoney((double)_settings.CallBonus, "Prime appel taxi");
			Notify(driver, $"Prime de prise en charge : +{_settings.CallBonus}€", (Type)1);
			AddStats(driver, _settings.CallBonus, 0f);
		}
		AddRideToHistory(driver.netId);
	}

	private List<TaxiCall> PendingCalls()
	{
		return _calls.Where((TaxiCall c) => c.TakenBy == 0).ToList();
	}

	private List<Player> AvailableDrivers()
	{
		return Nova.server.Players.Where((Player p) => IsTaxi(p) && (!_settings.RequireService || _onDuty.Contains(p.netId))).ToList();
	}

	private void RemoveCall(TaxiCall call, string reasonForDriver)
	{
		_calls.Remove(call);
		if (call.TakenBy != 0 && _rides.TryGetValue(call.TakenBy, out var value) && value.Call == call)
		{
			Player player = GetPlayer(call.TakenBy);
			ClearRideCheckpoint(player, value);
			_rides.Remove(call.TakenBy);
			Notify(player, reasonForDriver, (Type)2);
		}
	}

	private void SetRideCheckpoint(Player p, Ride ride, Vector3 position, Action<Player, Ride> onReached)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Expected O, but got Unknown
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		ClearRideCheckpoint(p, ride);
		NVehicleCheckpoint checkpoint = null;
		checkpoint = new NVehicleCheckpoint(p.netId, position, (Action<NVehicleCheckpoint, uint>)delegate
		{
			TryReach(p, ride, checkpoint, fromGame: true);
		});
		ride.Checkpoint = checkpoint;
		ride.Target = position;
		ride.OnReached = onReached;
		ride.LastWarning = DateTime.MinValue;
		p.CreateVehicleCheckpoint(checkpoint);
	}

	private void TryReach(Player p, Ride ride, NVehicleCheckpoint checkpoint, bool fromGame)
	{
		if (p == null || !_rides.TryGetValue(p.netId, out var value) || value != ride || ride.Checkpoint != checkpoint)
		{
			return;
		}
		string text = VehicleProblem(p);
		if (text != null)
		{
			if ((DateTime.Now - ride.LastWarning).TotalSeconds >= 5.0)
			{
				ride.LastWarning = DateTime.Now;
				Notify(p, text, (Type)2);
			}
		}
		else
		{
			ClearRideCheckpoint(p, ride);
			ride.OnReached(p, ride);
		}
	}

	internal void Tick()
	{
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		if (_rides.Count == 0)
		{
			return;
		}
		float num = Math.Max(2, _settings.TriggerRadius);
		foreach (Ride item in _rides.Values.ToList())
		{
			if (item.Checkpoint != null)
			{
				Player player = GetPlayer(item.DriverId);
				Vehicle val = DrivenVehicle(player);
				if (!((Object)(object)val == (Object)null) && Vector3.Distance(((Component)val).transform.position, item.Target) <= num)
				{
					TryReach(player, item, item.Checkpoint, fromGame: false);
				}
			}
		}
	}

	private static void ClearRideCheckpoint(Player p, Ride ride)
	{
		if (ride?.Checkpoint != null)
		{
			Nova.server.vehicleCheckpoints?.Remove(ride.Checkpoint);
			if (p != null)
			{
				p.DestroyVehicleCheckpoint(ride.Checkpoint);
			}
			ride.Checkpoint = null;
		}
	}

	private void CancelRide(Player p, bool verbose)
	{
		if (p == null || !_rides.TryGetValue(p.netId, out var value))
		{
			if (verbose && p != null)
			{
				Notify(p, "Vous n'avez aucune course en cours.", (Type)0);
			}
			return;
		}
		ClearRideCheckpoint(p, value);
		_rides.Remove(p.netId);
		if (value.Call != null && _calls.Contains(value.Call))
		{
			value.Call.TakenBy = 0u;
			Notify(GetPlayer(value.Call.CallerId), "Votre taxi a annulé. Votre appel est de nouveau en attente.", (Type)2);
		}
		Notify(p, "Course annulée.", (Type)0);
	}

	private void ResetPlayer(uint netId)
	{
		_onDuty.Remove(netId);
		if (_rides.TryGetValue(netId, out var value))
		{
			ClearRideCheckpoint(GetPlayer(netId), value);
			_rides.Remove(netId);
		}
		foreach (TaxiCall item in _calls.Where((TaxiCall c) => c.CallerId == netId).ToList())
		{
			RemoveCall(item, "Le client n'est plus disponible.");
		}
		foreach (TaxiCall item2 in _calls.Where((TaxiCall c) => c.TakenBy == netId))
		{
			item2.TakenBy = 0u;
		}
	}

	private void OnMinutePassed()
	{
		HashSet<uint> online = new HashSet<uint>(Nova.server.Players.Select((Player p) => p.netId));
		foreach (uint item in (from id in _onDuty.Concat(_rides.Keys)
			where !online.Contains(id)
			select id).ToList())
		{
			ResetPlayer(item);
		}
		foreach (uint item2 in (from c in _calls
			select c.CallerId into id
			where !online.Contains(id)
			select id).ToList())
		{
			ResetPlayer(item2);
		}
		if (_settings.CallExpireMinutes <= 0)
		{
			return;
		}
		foreach (TaxiCall item3 in _calls.Where((TaxiCall c) => (DateTime.Now - c.CreatedAt).TotalMinutes >= (double)_settings.CallExpireMinutes).ToList())
		{
			Notify(GetPlayer(item3.CallerId), "Votre appel de taxi a expiré.", (Type)2);
			RemoveCall(item3, "L'appel du client a expiré.");
		}
	}

	private int RidesLastHour(uint netId)
	{
		if (!_rideHistory.TryGetValue(netId, out var value))
		{
			return 0;
		}
		value.RemoveAll((DateTime d) => (DateTime.Now - d).TotalHours >= 1.0);
		return value.Count;
	}

	private void AddRideToHistory(uint netId)
	{
		if (!_rideHistory.TryGetValue(netId, out var value))
		{
			value = (_rideHistory[netId] = new List<DateTime>());
		}
		value.Add(DateTime.Now);
	}

	private static string RideDescription(Ride ride)
	{
		if (ride.Call != null)
		{
			return "aller chercher " + ride.Call.CallerName;
		}
		if (ride.Stage != 0)
		{
			return $"vers {ride.Dropoff.Name} ({ride.Fare}€)";
		}
		return "client à " + ride.Pickup.Name;
	}

	private async Task AddStats(Player p, int earned, float distance)
	{
		_ = 1;
		try
		{
			int characterId = p.character.Id;
			TaxiDriverStats? obj = (await ModEntity<TaxiDriverStats>.Query((Expression<Func<TaxiDriverStats, bool>>)((TaxiDriverStats s) => s.CharacterId == characterId))).FirstOrDefault() ?? new TaxiDriverStats
			{
				CharacterId = characterId
			};
			obj.Name = p.FullName;
			obj.Rides++;
			obj.Earned += earned;
			obj.Distance += (int)distance;
			await ((ModEntity<TaxiDriverStats>)obj).Save();
		}
		catch (Exception ex)
		{
			Logger.LogWarning(base.PluginInformations.SourceName, "Statistiques non enregistrées : " + ex.Message);
		}
	}

	private async Task MyStatsMenu(Player player)
	{
		int characterId = player.character.Id;
		TaxiDriverStats taxiDriverStats = (await ModEntity<TaxiDriverStats>.Query((Expression<Func<TaxiDriverStats, bool>>)((TaxiDriverStats s) => s.CharacterId == characterId))).FirstOrDefault() ?? new TaxiDriverStats();
		Panel obj = ((ModKit)this).PanelHelper.Create("Taxi - Mes statistiques", (PanelType)2, player, (Action)async delegate
		{
			await MyStatsMenu(player);
		});
		((UIPanel)obj).AddTabLine($"Courses effectuées : {taxiDriverStats.Rides}", (Action<UIPanel>)delegate
		{
		});
		((UIPanel)obj).AddTabLine($"Argent gagné : {taxiDriverStats.Earned}€", (Action<UIPanel>)delegate
		{
		});
		((UIPanel)obj).AddTabLine($"Distance parcourue avec client : {(double)taxiDriverStats.Distance / 1000.0:0.0} km", (Action<UIPanel>)delegate
		{
		});
		((UIPanel)obj).AddTabLine($"Courses cette heure : {RidesLastHour(player.netId)}" + ((_settings.MaxRidesPerHour > 0) ? $" / {_settings.MaxRidesPerHour}" : ""), (Action<UIPanel>)delegate
		{
		});
		obj.PreviousButton();
		obj.CloseButton();
		obj.Display();
	}

	private async Task LeaderboardMenu(Player player, bool staff)
	{
		List<TaxiDriverStats> list = (await ModEntity<TaxiDriverStats>.QueryAll()).OrderByDescending((TaxiDriverStats s) => s.Earned).ToList();
		string action = "";
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Classement", (PanelType)2, player, (Action)async delegate
		{
			await LeaderboardMenu(player, staff);
		});
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucune course effectuée pour le moment", (Action<UIPanel>)delegate
			{
			});
		}
		int num = 1;
		foreach (TaxiDriverStats stats in list.Take(staff ? 50 : 10))
		{
			((UIPanel)panel).AddTabLine($"#{num++} {stats.Name} - {stats.Rides} courses - {stats.Earned}€", (Action<UIPanel>)async delegate
			{
				bool flag = staff && action == "reset";
				if (flag)
				{
					flag = await ((ModEntity<TaxiDriverStats>)stats).Delete();
				}
				if (flag)
				{
					Notify(player, "Statistiques de " + stats.Name + " remises à zéro.", (Type)1);
					panel.Refresh();
				}
			});
		}
		if (staff && list.Count > 0)
		{
			((UIPanel)panel).AddButton(Col("Remettre à zéro", "#e74c3c"), (Action<UIPanel>)delegate
			{
				action = "reset";
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private static bool IsStaff(Player player)
	{
		if (player != null)
		{
			return player.IsAdmin;
		}
		return false;
	}

	public void OpenAdminMenu(Player player)
	{
		if (IsStaff(player))
		{
			Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Configuration (staff)", (PanelType)2, player, (Action)delegate
			{
				OpenAdminMenu(player);
			});
			panel.TextLines.Add(Col("TaxiJob v" + base.PluginInformations.Version + " - By Matheo Mercier", "#f1c40f"));
			((UIPanel)panel).AddTabLine("Paramètres du job (paie, entreprise, règles...)", (Action<UIPanel>)delegate
			{
				SettingsMenu(player);
			});
			((UIPanel)panel).AddTabLine($"Points de course ({_points.Count((TaxiPoint p) => p.Enabled)} actifs / {_points.Count})", (Action<UIPanel>)delegate
			{
				PointsMenu(player);
			});
			((UIPanel)panel).AddTabLine("Véhicules autorisés (" + ((_settings.AllowedModelIds.Count == 0) ? "tous" : _settings.AllowedModelIds.Count.ToString()) + ")", (Action<UIPanel>)delegate
			{
				VehiclesMenu(player);
			});
			((UIPanel)panel).AddTabLine("Centrales taxi (points bleus) : placer ici", (Action<UIPanel>)async delegate
			{
				await PlaceStationMenu(player);
			});
			((UIPanel)panel).AddTabLine("Centrales taxi : liste des points placés", (Action<UIPanel>)async delegate
			{
				await StationPointsMenu(player);
			});
			((UIPanel)panel).AddTabLine("Centrales taxi : modèles", (Action<UIPanel>)async delegate
			{
				await StationModelsMenu(player, forEdit: true);
			});
			((UIPanel)panel).AddTabLine($"Courses en cours ({_rides.Count})", (Action<UIPanel>)delegate
			{
				ActiveRidesMenu(player);
			});
			((UIPanel)panel).AddTabLine($"Appels clients en attente ({PendingCalls().Count})", (Action<UIPanel>)delegate
			{
				AdminCallsMenu(player);
			});
			((UIPanel)panel).AddTabLine("Classement / remise à zéro des statistiques", (Action<UIPanel>)async delegate
			{
				await LeaderboardMenu(player, staff: true);
			});
			panel.NextButton("Ouvrir", (Action)delegate
			{
				((UIPanel)panel).SelectTab();
			});
			panel.CloseButton();
			panel.Display();
		}
	}

	private void SettingsMenu(Player player)
	{
		if (!IsStaff(player))
		{
			return;
		}
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Paramètres", (PanelType)2, player, (Action)delegate
		{
			SettingsMenu(player);
		});
		foreach (SettingField field in SettingFields)
		{
			((UIPanel)panel).AddTabLine(field.Label + " : " + Col(field.Display(_settings), "#f1c40f"), (Action<UIPanel>)async delegate
			{
				if (field.IsBool)
				{
					field.Set(_settings, (field.Get(_settings) != 1) ? 1 : 0);
					if (await SaveSettings(player))
					{
						Notify(player, field.Label + " : " + field.Display(_settings), (Type)1);
					}
					panel.Refresh();
				}
				else
				{
					EditSettingMenu(player, field);
				}
			});
		}
		panel.NextButton("Modifier", (Action)delegate
		{
			((UIPanel)panel).SelectTab();
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void EditSettingMenu(Player player, SettingField field)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Modifier un paramètre", (PanelType)1, player, (Action)delegate
		{
			EditSettingMenu(player, field);
		});
		panel.TextLines.Add(field.Label);
		panel.TextLines.Add($"Valeur actuelle : {field.Display(_settings)} (entre {field.Min} et {field.Max})");
		((UIPanel)panel).inputPlaceholder = field.Get(_settings).ToString();
		panel.PreviousButtonWithAction("Valider", (Func<Task<bool>>)async delegate
		{
			if (!int.TryParse(((UIPanel)panel).inputText?.Trim(), out var result) || result < field.Min || result > field.Max)
			{
				Notify(player, $"Valeur invalide : entrez un nombre entre {field.Min} et {field.Max}.", (Type)3);
				return false;
			}
			field.Set(_settings, result);
			if (!(await SaveSettings(player)))
			{
				return false;
			}
			Notify(player, field.Label + " : " + field.Display(_settings), (Type)1);
			return true;
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void PointsMenu(Player player)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		if (!IsStaff(player))
		{
			return;
		}
		Vector3 val = Position(player);
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Points de course", (PanelType)2, player, (Action)delegate
		{
			PointsMenu(player);
		});
		panel.TextLines.Add("Les clients PNJ sont pris en charge et déposés sur ces points (en voiture).");
		if (_points.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucun point : placez-vous en voiture et « Ajouter ici »", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (TaxiPoint point in _points.OrderBy((TaxiPoint p) => p.Id))
		{
			string text = (point.Enabled ? point.TypeLabel : Col("désactivé", "#e74c3c"));
			((UIPanel)panel).AddTabLine($"[{point.Id}] {point.Name} - {text} - {(int)Vector3.Distance(val, point.Position)} m", (Action<UIPanel>)delegate
			{
				PointMenu(player, point);
			});
		}
		if (_points.Count > 0)
		{
			panel.NextButton("Gérer", (Action)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.NextButton("Ajouter ici", (Action)delegate
		{
			CreatePointMenu(player);
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void CreatePointMenu(Player player)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Nouveau point de course", (PanelType)1, player, (Action)delegate
		{
			CreatePointMenu(player);
		});
		panel.TextLines.Add("Nom du lieu (ex. Gare, Hôpital, Mairie...). Le point est créé à votre position.");
		panel.TextLines.Add("Placez-vous sur la route : le chauffeur doit pouvoir y passer en voiture.");
		((UIPanel)panel).inputPlaceholder = $"Arrêt {_points.Count + 1}";
		panel.PreviousButtonWithAction("Créer ici", (Func<Task<bool>>)async delegate
		{
			string name = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(name))
			{
				name = $"Arrêt {_points.Count + 1}";
			}
			Vector3 val = Position(player);
			if (!(await ((ModEntity<TaxiPoint>)new TaxiPoint
			{
				Name = name,
				X = val.x,
				Y = val.y,
				Z = val.z,
				IsPickup = true,
				IsDropoff = true,
				Enabled = true
			}).Save()))
			{
				Notify(player, "Erreur lors de l'enregistrement du point.", (Type)3);
				return false;
			}
			await ReloadPoints();
			Notify(player, "Point « " + name + " » créé à votre position.", (Type)1);
			return true;
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void PointMenu(Player player, TaxiPoint point)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Point « " + point.Name + " »", (PanelType)2, player, (Action)delegate
		{
			PointMenu(player, point);
		});
		panel.TextLines.Add("Type : " + point.TypeLabel + " - " + (point.Enabled ? "actif" : "désactivé"));
		((UIPanel)panel).AddTabLine("Renommer", (Action<UIPanel>)delegate
		{
			RenamePointMenu(player, point);
		});
		((UIPanel)panel).AddTabLine("Type : " + Col(point.TypeLabel, "#f1c40f") + " (changer)", (Action<UIPanel>)async delegate
		{
			if (point.IsPickup && point.IsDropoff)
			{
				point.IsDropoff = false;
			}
			else if (point.IsPickup)
			{
				point.IsPickup = false;
				point.IsDropoff = true;
			}
			else
			{
				point.IsPickup = true;
				point.IsDropoff = true;
			}
			await SavePoint(player, point, "Type : " + point.TypeLabel);
			panel.Refresh();
		});
		((UIPanel)panel).AddTabLine(point.Enabled ? Col("Désactiver", "#e74c3c") : Col("Activer", "#2ecc71"), (Action<UIPanel>)async delegate
		{
			point.Enabled = !point.Enabled;
			await SavePoint(player, point, point.Enabled ? "Point activé" : "Point désactivé");
			panel.Refresh();
		});
		((UIPanel)panel).AddTabLine("Déplacer à ma position", (Action<UIPanel>)async delegate
		{
			Vector3 val = Position(player);
			point.X = val.x;
			point.Y = val.y;
			point.Z = val.z;
			await SavePoint(player, point, "Point déplacé à votre position");
		});
		((UIPanel)panel).AddTabLine("Se téléporter au point", (Action<UIPanel>)delegate
		{
			//IL_0011: Unknown result type (might be due to invalid IL or missing references)
			player.setup.TargetSetPosition(point.Position);
		});
		((UIPanel)panel).AddTabLine(Col("Supprimer", "#e74c3c"), (Action<UIPanel>)async delegate
		{
			if (await ((ModEntity<TaxiPoint>)point).Delete())
			{
				await ReloadPoints();
				Notify(player, "Point « " + point.Name + " » supprimé.", (Type)1);
				PointsMenu(player);
			}
			else
			{
				Notify(player, "Erreur lors de la suppression.", (Type)3);
			}
		});
		panel.NextButton("Valider", (Action)delegate
		{
			((UIPanel)panel).SelectTab();
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void RenamePointMenu(Player player, TaxiPoint point)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Renommer le point", (PanelType)1, player, (Action)delegate
		{
			RenamePointMenu(player, point);
		});
		panel.TextLines.Add("Nouveau nom pour « " + point.Name + " »");
		((UIPanel)panel).inputPlaceholder = point.Name;
		panel.PreviousButtonWithAction("Valider", (Func<Task<bool>>)async delegate
		{
			string text = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			point.Name = text;
			return await SavePoint(player, point, "Point renommé en « " + text + " »");
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private async Task<bool> SavePoint(Player player, TaxiPoint point, string successMessage)
	{
		bool ok = await ((ModEntity<TaxiPoint>)point).Save();
		await ReloadPoints();
		Notify(player, ok ? successMessage : "Erreur lors de l'enregistrement du point.", (Type)(ok ? 1 : 3));
		return ok;
	}

	private void VehiclesMenu(Player player)
	{
		if (!IsStaff(player))
		{
			return;
		}
		List<int> allowedModelIds = _settings.AllowedModelIds;
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Véhicules autorisés", (PanelType)2, player, (Action)delegate
		{
			VehiclesMenu(player);
		});
		panel.TextLines.Add("Liste vide : tous les véhicules sont acceptés.");
		if (allowedModelIds.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Tous les véhicules sont autorisés", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (int modelId in allowedModelIds)
		{
			((UIPanel)panel).AddTabLine(string.Format("[{0}] {1} - {2}", modelId, ModelName(modelId), Col("retirer", "#e74c3c")), (Action<UIPanel>)async delegate
			{
				_settings.AllowedModelIds = _settings.AllowedModelIds.Where((int id) => id != modelId).ToList();
				if (await SaveSettings(player))
				{
					Notify(player, ModelName(modelId) + " retiré des taxis.", (Type)1);
				}
				panel.Refresh();
			});
		}
		((UIPanel)panel).AddButton("Ajouter mon véhicule", (Action<UIPanel>)async delegate
		{
			int modelId2 = DrivenModelId(player);
			if (modelId2 == -1)
			{
				Notify(player, "Montez au volant du véhicule à autoriser.", (Type)2);
			}
			else if (modelId2 < 0)
			{
				Notify(player, "Modèle inconnu (véhicule non enregistré). Utilisez plutôt « Véhicule de l'entreprise taxi obligatoire ».", (Type)2);
			}
			else
			{
				if (!_settings.AllowedModelIds.Contains(modelId2))
				{
					_settings.AllowedModelIds = _settings.AllowedModelIds.Append(modelId2).ToList();
					if (await SaveSettings(player))
					{
						Notify(player, ModelName(modelId2) + " ajouté aux taxis.", (Type)1);
					}
				}
				panel.Refresh();
			}
		});
		if (allowedModelIds.Count > 0)
		{
			((UIPanel)panel).AddButton(Col("Retirer", "#e74c3c"), (Action<UIPanel>)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private static string ModelName(int modelId)
	{
		try
		{
			return VehicleUtils.GetModelNameByModelId(modelId);
		}
		catch
		{
			return $"Modèle {modelId}";
		}
	}

	private void ActiveRidesMenu(Player player)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Courses en cours", (PanelType)2, player, (Action)delegate
		{
			ActiveRidesMenu(player);
		});
		if (_rides.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucune course en cours", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (Ride ride in _rides.Values.ToList())
		{
			Player driver = GetPlayer(ride.DriverId);
			Panel obj = panel;
			Player obj2 = driver;
			((UIPanel)obj).AddTabLine((((obj2 != null) ? obj2.FullName : null) ?? "?") + " : " + RideDescription(ride), (Action<UIPanel>)delegate
			{
				if (driver != null)
				{
					CancelRide(driver, verbose: false);
					Notify(driver, "Votre course a été annulée par le staff.", (Type)2);
				}
				else
				{
					_rides.Remove(ride.DriverId);
				}
				Notify(player, "Course annulée.", (Type)1);
				panel.Refresh();
			});
		}
		if (_rides.Count > 0)
		{
			((UIPanel)panel).AddButton(Col("Annuler la course", "#e74c3c"), (Action<UIPanel>)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void AdminCallsMenu(Player player)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Appels clients", (PanelType)2, player, (Action)delegate
		{
			AdminCallsMenu(player);
		});
		if (_calls.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucun appel", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (TaxiCall call in _calls.ToList())
		{
			object obj;
			if (call.TakenBy == 0)
			{
				obj = "en attente";
			}
			else
			{
				Player player2 = GetPlayer(call.TakenBy);
				obj = "pris par " + (((player2 != null) ? player2.FullName : null) ?? "?");
			}
			string arg = (string)obj;
			((UIPanel)panel).AddTabLine($"#{call.Id} {call.CallerName} - {arg}", (Action<UIPanel>)delegate
			{
				Notify(GetPlayer(call.CallerId), "Votre appel de taxi a été annulé par le staff.", (Type)2);
				RemoveCall(call, "L'appel a été annulé par le staff.");
				panel.Refresh();
			});
		}
		if (_calls.Count > 0)
		{
			((UIPanel)panel).AddButton(Col("Supprimer l'appel", "#e74c3c"), (Action<UIPanel>)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public async Task PlaceStationMenu(Player player)
	{
		if (!IsStaff(player))
		{
			return;
		}
		List<TaxiStation> list = await ModEntity<TaxiStation>.QueryAll();
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Placer une centrale", (PanelType)2, player, (Action)async delegate
		{
			await PlaceStationMenu(player);
		});
		panel.TextLines.Add("Une centrale est un point bleu où les chauffeurs ouvrent le menu taxi (service, courses...).");
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucun modèle : créez-en un (« Nouveau modèle »)", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (TaxiStation model in list)
		{
			((UIPanel)panel).AddTabLine($"[{model.Id}] {model.PatternName}", (Action<UIPanel>)async delegate
			{
				model.TypeName = "TaxiStation";
				model.Context = (ModKit)(object)this;
				if (await ((ModKit)this).PointHelper.CreateNPoint(player, (PatternData)(object)model))
				{
					Notify(player, "Centrale « " + model.PatternName + " » placée à votre position.", (Type)1);
				}
				else
				{
					Notify(player, "Erreur lors de la création du point.", (Type)3);
				}
			});
		}
		if (list.Count > 0)
		{
			panel.NextButton("Placer ici", (Action)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.NextButton("Nouveau modèle", (Action)delegate
		{
			CreateStationModel(player);
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public void CreateStationModel(Player player)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Nouveau modèle de centrale", (PanelType)1, player, (Action)delegate
		{
			CreateStationModel(player);
		});
		panel.TextLines.Add("Nom de la centrale (affiché aux joueurs)");
		((UIPanel)panel).inputPlaceholder = "Centrale taxi";
		panel.PreviousButtonWithAction("Créer", (Func<Task<bool>>)async delegate
		{
			string name = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(name))
			{
				name = "Centrale taxi";
			}
			if (!(await ((ModEntity<TaxiStation>)new TaxiStation(isCreated: false)
			{
				PatternName = name
			}).Save()))
			{
				Notify(player, "Erreur lors de l'enregistrement.", (Type)3);
				return false;
			}
			Notify(player, "Modèle « " + name + " » créé. Choisissez-le puis « Placer ici ».", (Type)1);
			return true;
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public async Task StationModelsMenu(Player player, bool forEdit)
	{
		List<TaxiStation> list = await ModEntity<TaxiStation>.QueryAll();
		string action = "";
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Modèles de centrale", (PanelType)2, player, (Action)async delegate
		{
			await StationModelsMenu(player, forEdit);
		});
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucun modèle", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (TaxiStation model in list)
		{
			((UIPanel)panel).AddTabLine($"[{model.Id}] {model.PatternName}", (Action<UIPanel>)async delegate
			{
				model.TypeName = "TaxiStation";
				model.Context = (ModKit)(object)this;
				if (action == "rename")
				{
					RenameStationModel(player, model);
				}
				else if (action == "delete")
				{
					await ((ModKit)this).PointHelper.DeleteNPointsByPattern(player, (PatternData)(object)model);
					if (await ((ModEntity<TaxiStation>)model).Delete())
					{
						Notify(player, "Modèle « " + model.PatternName + " » et ses centrales supprimés.", (Type)1);
					}
					else
					{
						Notify(player, "Erreur lors de la suppression.", (Type)3);
					}
					panel.Refresh();
				}
			});
		}
		if (list.Count > 0 && forEdit)
		{
			panel.NextButton("Renommer", (Action)delegate
			{
				action = "rename";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton(Col("Supprimer", "#e74c3c"), (Action<UIPanel>)delegate
			{
				action = "delete";
				((UIPanel)panel).SelectTab();
			});
		}
		panel.NextButton("Nouveau modèle", (Action)delegate
		{
			CreateStationModel(player);
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void RenameStationModel(Player player, TaxiStation model)
	{
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Renommer la centrale", (PanelType)1, player, (Action)delegate
		{
			RenameStationModel(player, model);
		});
		panel.TextLines.Add("Nouveau nom pour « " + model.PatternName + " »");
		((UIPanel)panel).inputPlaceholder = model.PatternName ?? "Centrale taxi";
		panel.PreviousButtonWithAction("Valider", (Func<Task<bool>>)async delegate
		{
			string text = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			model.PatternName = text;
			return await ((ModEntity<TaxiStation>)model).Save();
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public async Task StationPointsMenu(Player player)
	{
		List<NPoint> points = await ModEntity<NPoint>.Query((Expression<Func<NPoint, bool>>)((NPoint p) => p.TypeName == "TaxiStation"));
		Dictionary<int, string> dictionary = (await ModEntity<TaxiStation>.QueryAll()).ToDictionary((TaxiStation p) => p.Id, (TaxiStation p) => p.PatternName);
		string action = "";
		Panel panel = ((ModKit)this).PanelHelper.Create("Taxi - Centrales placées", (PanelType)2, player, (Action)async delegate
		{
			await StationPointsMenu(player);
		});
		if (points.Count == 0)
		{
			((UIPanel)panel).AddTabLine("Aucune centrale placée", (Action<UIPanel>)delegate
			{
			});
		}
		foreach (NPoint point in points)
		{
			string value;
			string arg = (dictionary.TryGetValue(point.PatternId, out value) ? value : "?");
			((UIPanel)panel).AddTabLine($"Point #{point.Id} - {arg}", (Action<UIPanel>)async delegate
			{
				switch (action)
				{
				case "tp":
					((ModKit)this).PointHelper.PlayerSetPositionToNPoint(player, point);
					break;
				case "move":
					if (await ((ModKit)this).PointHelper.SetNPointPosition(player, point))
					{
						Notify(player, "Centrale déplacée à votre position.", (Type)1);
					}
					break;
				case "delete":
					await ((ModKit)this).PointHelper.DeleteNPoint(point);
					Notify(player, $"Centrale #{point.Id} supprimée.", (Type)1);
					panel.Refresh();
					break;
				}
			});
		}
		if (points.Count > 0)
		{
			((UIPanel)panel).AddButton("Se téléporter", (Action<UIPanel>)delegate
			{
				action = "tp";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton("Déplacer ici", (Action<UIPanel>)delegate
			{
				action = "move";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton(Col("Supprimer", "#e74c3c"), (Action<UIPanel>)delegate
			{
				action = "delete";
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private static string Col(string text, string hex)
	{
		return "<color=" + hex + ">" + text + "</color>";
	}

	private static string YesNo(bool value)
	{
		if (!value)
		{
			return Col("Non", "#e74c3c");
		}
		return Col("Oui", "#2ecc71");
	}

	private static string FormatDuration(double seconds)
	{
		int num = (int)Math.Round(seconds);
		if (num < 60)
		{
			return $"{num} s";
		}
		return $"{num / 60} min {num % 60:00} s";
	}

	private static void Notify(Player player, string message, Type type)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		if (player != null)
		{
			player.Notify("Taxi", message, type, 6f);
		}
	}

	private static Player GetPlayer(uint netId)
	{
		return ((IEnumerable<Player>)Nova.server.Players).FirstOrDefault((Func<Player, bool>)((Player p) => p.netId == netId));
	}

	private static Vector3 Position(Player player)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		Vehicle val = DrivenVehicle(player);
		if (!((Object)(object)val != (Object)null))
		{
			return ((Component)player.setup).transform.position;
		}
		return ((Component)val).transform.position;
	}

	private T Pick<T>(List<T> list)
	{
		return list[_rng.Next(list.Count)];
	}
}
