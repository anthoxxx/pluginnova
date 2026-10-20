using System.Threading.Tasks;
using Life.Network;
using ModKit;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using SQLite;

namespace TaxiJob;

public class TaxiStation : ModEntity<TaxiStation>, PatternData
{
	[AutoIncrement]
	[PrimaryKey]
	public int Id { get; set; }

	public string PatternName { get; set; }

	[Ignore]
	public string TypeName { get; set; } = "TaxiStation";


	[Ignore]
	public ModKit Context { get; set; }

	public TaxiStation()
	{
	}

	public TaxiStation(bool isCreated)
	{
		TypeName = "TaxiStation";
	}

	public void OnPlayerTrigger(Player player)
	{
		TaxiJobPlugin.Instance?.OpenDriverMenu(player);
	}

	public async Task SetProperties(int id)
	{
		TaxiStation taxiStation = await ModEntity<TaxiStation>.Query(id);
		Id = id;
		TypeName = "TaxiStation";
		PatternName = taxiStation?.PatternName;
	}

	public void CreateOrGenerate(Player player)
	{
		TaxiJobPlugin.Instance?.OpenAdminMenu(player);
	}

	public void SetPatternData(Player player)
	{
		TaxiJobPlugin.Instance?.CreateStationModel(player);
	}

	public Task GetPatternData(Player player, bool forEdit)
	{
		return TaxiJobPlugin.Instance?.StationModelsMenu(player, forEdit) ?? Task.CompletedTask;
	}

	public Task GetNPoints(Player player)
	{
		return TaxiJobPlugin.Instance?.StationPointsMenu(player) ?? Task.CompletedTask;
	}
}
