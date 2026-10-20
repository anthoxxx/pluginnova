using ModKit.ORM;
using SQLite;
using UnityEngine;

namespace TaxiJob;

public class TaxiPoint : ModEntity<TaxiPoint>
{
	[AutoIncrement]
	[PrimaryKey]
	public int Id { get; set; }

	public string Name { get; set; }

	public float X { get; set; }

	public float Y { get; set; }

	public float Z { get; set; }

	public bool IsPickup { get; set; } = true;


	public bool IsDropoff { get; set; } = true;


	public bool Enabled { get; set; } = true;


	[Ignore]
	public Vector3 Position => new Vector3(X, Y, Z);

	[Ignore]
	public string TypeLabel
	{
		get
		{
			if (!IsPickup || !IsDropoff)
			{
				if (!IsPickup)
				{
					return "dépose";
				}
				return "prise en charge";
			}
			return "prise en charge + dépose";
		}
	}
}
