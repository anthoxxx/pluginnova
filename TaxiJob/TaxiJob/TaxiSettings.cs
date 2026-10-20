using System.Collections.Generic;
using System.Linq;
using ModKit.ORM;
using SQLite;

namespace TaxiJob;

public class TaxiSettings : ModEntity<TaxiSettings>
{
	[AutoIncrement]
	[PrimaryKey]
	public int Id { get; set; }

	public int BizId { get; set; }

	public bool RequireService { get; set; } = true;


	public int BasePay { get; set; } = 50;


	public int PayPerKm { get; set; } = 150;


	public int MinPay { get; set; } = 80;


	public int MaxPay { get; set; } = 600;


	public int TipPercent { get; set; } = 20;


	public int ReferenceSpeedKmh { get; set; } = 50;


	public int MinRideDistance { get; set; } = 300;


	public bool AutoChain { get; set; }

	public int MaxRidesPerHour { get; set; }

	public bool AllowPlayerCalls { get; set; } = true;


	public int CallExpireMinutes { get; set; } = 10;


	public int CallBonus { get; set; }

	public bool RequireVehicle { get; set; } = true;


	public bool RequireBizVehicle { get; set; }

	public int TriggerRadius { get; set; } = 8;


	public string AllowedModels { get; set; } = "";


	[Ignore]
	public List<int> AllowedModelIds
	{
		get
		{
			int result;
			return (from x in (AllowedModels ?? "").Split(new char[1] { ',' })
				select (!int.TryParse(x.Trim(), out result)) ? (-1) : result into id
				where id >= 0
				select id).Distinct().ToList();
		}
		set
		{
			AllowedModels = string.Join(",", value);
		}
	}
}
