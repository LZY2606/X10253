namespace PowerSequencer.Web;

public static class SampleData
{
    public const string Valid = """
{
  "rail": "VDD_1V2",
  "signalKind": "voltage",
  "sampleClock": { "value": 10, "unit": "us" },
  "clockJitter": { "value": 0.5, "unit": "us" },
  "measurementError": { "value": 15, "unit": "mV" },
  "samples": [
    { "time": { "value": 0, "unit": "us" }, "value": { "value": 0.0, "unit": "V" } },
    { "time": { "value": 10, "unit": "us" }, "value": { "value": 0.4, "unit": "V" } },
    { "time": { "value": 20, "unit": "us" }, "value": { "value": 1.2, "unit": "V" } },
    { "time": { "value": 30, "unit": "us" }, "value": { "value": 1200, "unit": "mV" } },
    { "time": { "value": 120, "unit": "us" }, "value": { "value": 0.85, "unit": "V" } },
    { "time": { "value": 130, "unit": "us" }, "value": { "value": 1.2, "unit": "V" } },
    { "time": { "value": 200, "unit": "us" }, "value": { "value": 0.0, "unit": "V" } }
  ]
}
""";

    public const string Malformed = """
{
  "rail": "VDD_1V8",
  "signalKind": "voltage",
  "sampleClock": { "value": 10, "unit": "us" },
  "clockJitter": { "value": 0.5, "unit": "us" },
  "measurementError": { "value": 15, "unit": "mV" },
  "samples": [
    { "time": { "value": 0, "unit": "us" }
      , "value": { "value": 0.0, "unit": "V" } },
    { "time": { "value": 10, "unit": "us" }, "value": { "value": 50, "unit": "counts" } },
    { "time": { "value": 20, "unit": "us" }, "value": { "value": 1.8, "unit": "V" } }
  ]
}
""";
}
