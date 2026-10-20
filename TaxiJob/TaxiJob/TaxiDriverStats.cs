using ModKit.ORM;
using SQLite;

namespace TaxiJob;

public class TaxiDriverStats : ModEntity<TaxiDriverStats>
{
	[AutoIncrement]
	[PrimaryKey]
	public int Id { get; set; }

	public int CharacterId { get; set; }

	public string Name { get; set; }

	public int Rides { get; set; }

	public int Earned { get; set; }

	public int Distance { get; set; }
}
