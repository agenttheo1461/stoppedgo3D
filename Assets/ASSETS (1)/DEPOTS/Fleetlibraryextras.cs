using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET LIBRARY EXTRAS — the per-series/per-bus data the Fleet Library browser
//  reads on top of your existing FleetRosterData. Deliberately a SEPARATE asset
//  rather than added fields on BusSeriesData/FleetRosterData — cross-referenced
//  by seriesName (must match a series entry's own name exactly) and fleetNumber,
//  so it never needs to touch those classes at all.
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class FleetLiveryOption
{
    public string liveryName = "Base";
    [Tooltip("Small preview swatch shown in the browser. Optional — falls back to fullTexture if unset.")]
    public Texture2D thumbnail;
    [Tooltip("The actual livery texture applied to the bus's material when selected.")]
    public Texture2D fullTexture;
}

[System.Serializable]
public class IndividualBusEntry
{
    public int fleetNumber;

    [TextArea(2, 6)]
    [Tooltip("Freeform flavor/maintenance/character notes for this specific unit, e.g. \"1701 has a deeper engine than others in the series but everyone loves its Faucet wrap.\"")]
    public string notes;

    [Tooltip("A wrap unique to just THIS bus, on top of whatever fleet-wide liveries are available to the whole series.")]
    public FleetLiveryOption specificWrap;

    [Tooltip("Which livery name this bus is currently wearing — matched against the series' fleetLiveries list plus this entry's own specificWrap. Empty = default/base.")]
    public string currentLiveryName = "";
}

[CreateAssetMenu(menuName = "Transit/Fleet Library Extras", fileName = "FleetLibraryExtras")]
public class FleetLibraryExtras : ScriptableObject
{
    [System.Serializable]
    public class SeriesExtras
    {
        [Tooltip("Must exactly match a FleetRosterData series entry's seriesName.")]
        public string seriesName;

        [Tooltip("Liveries any bus in this series can wear.")]
        public List<FleetLiveryOption> fleetLiveries = new();

        [Tooltip("Sparse — only add an entry here for a specific bus number that has notes, a unique wrap, or a non-default livery selection. Buses with no entry just show the fleet liveries and no notes.")]
        public List<IndividualBusEntry> individualEntries = new();

        public IndividualBusEntry GetEntry(int fleetNumber) => individualEntries.Find(e => e.fleetNumber == fleetNumber);

        public IndividualBusEntry GetOrCreateEntry(int fleetNumber)
        {
            var existing = GetEntry(fleetNumber);
            if (existing != null) return existing;
            var fresh = new IndividualBusEntry { fleetNumber = fleetNumber };
            individualEntries.Add(fresh);
            return fresh;
        }
    }

    public List<SeriesExtras> seriesExtras = new();

    public SeriesExtras GetExtrasFor(string seriesName) => seriesExtras.Find(s => s.seriesName == seriesName);

    public SeriesExtras GetOrCreateExtrasFor(string seriesName)
    {
        var existing = GetExtrasFor(seriesName);
        if (existing != null) return existing;
        var fresh = new SeriesExtras { seriesName = seriesName };
        seriesExtras.Add(fresh);
        return fresh;
    }

    /// <summary>All liveries selectable for a given bus: the series' fleet-wide
    /// options plus this specific bus's own unique wrap, if it has one.</summary>
    public List<FleetLiveryOption> GetSelectableLiveries(string seriesName, int fleetNumber)
    {
        var result = new List<FleetLiveryOption>();
        var series = GetExtrasFor(seriesName);
        if (series == null) return result;

        if (series.fleetLiveries != null) result.AddRange(series.fleetLiveries);

        var entry = series.GetEntry(fleetNumber);
        if (entry?.specificWrap != null && !string.IsNullOrEmpty(entry.specificWrap.liveryName))
            result.Add(entry.specificWrap);

        return result;
    }
}