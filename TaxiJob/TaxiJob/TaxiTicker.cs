using System;
using UnityEngine;

namespace TaxiJob;

internal sealed class TaxiTicker : MonoBehaviour
{
	private const float Interval = 0.25f;

	private TaxiJobPlugin _plugin;

	private float _next;

	public static void Start(TaxiJobPlugin plugin)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Expected O, but got Unknown
		GameObject val = new GameObject("TaxiJobTicker");
		Object.DontDestroyOnLoad((Object)val);
		val.AddComponent<TaxiTicker>()._plugin = plugin;
	}

	private void Update()
	{
		if (Time.time < _next)
		{
			return;
		}
		_next = Time.time + 0.25f;
		try
		{
			_plugin?.Tick();
		}
		catch (Exception arg)
		{
			Debug.LogError((object)$"[TaxiJob] {arg}");
		}
	}
}
