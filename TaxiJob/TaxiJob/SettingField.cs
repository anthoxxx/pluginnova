using System;

namespace TaxiJob;

internal sealed class SettingField
{
	public string Label;

	public bool IsBool;

	public int Min;

	public int Max;

	public Func<TaxiSettings, int> Get;

	public Action<TaxiSettings, int> Set;

	public string Display(TaxiSettings s)
	{
		if (!IsBool)
		{
			return Get(s).ToString();
		}
		if (Get(s) != 1)
		{
			return "Non";
		}
		return "Oui";
	}

	public static SettingField Int(string label, Func<TaxiSettings, int> get, Action<TaxiSettings, int> set, int min, int max)
	{
		return new SettingField
		{
			Label = label,
			Get = get,
			Set = set,
			Min = min,
			Max = max
		};
	}

	public static SettingField Bool(string label, Func<TaxiSettings, bool> get, Action<TaxiSettings, bool> set)
	{
		return new SettingField
		{
			Label = label,
			IsBool = true,
			Min = 0,
			Max = 1,
			Get = (TaxiSettings s) => get(s) ? 1 : 0,
			Set = delegate(TaxiSettings s, int v)
			{
				set(s, v == 1);
			}
		};
	}
}
