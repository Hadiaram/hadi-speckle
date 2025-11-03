using System;
using Objects.Geometry;
using Speckle.Core.Kits;
using Speckle.Core.Logging;

namespace Objects.Converter.CSI;

public partial class ConverterCSI
{
  // ETABS minimum frame length tolerance (in model units)
  // Frames shorter than this will be skipped to avoid API errors
  // Based on ETABS numerical precision - typically rejects frames < 0.1 inches
  private const double MIN_FRAME_LENGTH_TOLERANCE = 0.1; // 0.1 inches, feet, meters, etc.

  public string LineToNative(Line line)
  {
    string newFrame = "";
    Point lineStart = line.start;
    Point end2Node = line.end;

    var modelUnits = ModelUnits();
    var end1NodeSf = Units.GetConversionFactor(lineStart.units, modelUnits);
    var end2NodeSf = Units.GetConversionFactor(end2Node.units, modelUnits);

    // Convert coordinates to model units
    double x1 = lineStart.x * end1NodeSf;
    double y1 = lineStart.y * end1NodeSf;
    double z1 = lineStart.z * end1NodeSf;
    double x2 = end2Node.x * end2NodeSf;
    double y2 = end2Node.y * end2NodeSf;
    double z2 = end2Node.z * end2NodeSf;

    // Calculate frame length in model units
    double dx = x2 - x1;
    double dy = y2 - y1;
    double dz = z2 - z1;
    double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

    SpeckleLog.Logger.Information("🔍 LineToNative called:");
    SpeckleLog.Logger.Information("   Start: ({X}, {Y}, {Z}) {Units}",
      lineStart.x, lineStart.y, lineStart.z, lineStart.units);
    SpeckleLog.Logger.Information("   End: ({X}, {Y}, {Z}) {Units}",
      end2Node.x, end2Node.y, end2Node.z, end2Node.units);
    SpeckleLog.Logger.Information("   Model units: {Units}", modelUnits);
    SpeckleLog.Logger.Information("   Conversion factors: start={Start}, end={End}",
      end1NodeSf, end2NodeSf);
    SpeckleLog.Logger.Information("   Converted start: ({X}, {Y}, {Z})", x1, y1, z1);
    SpeckleLog.Logger.Information("   Converted end: ({X}, {Y}, {Z})", x2, y2, z2);
    SpeckleLog.Logger.Information("   Frame length: {Length} {Units}", length, modelUnits);

    // Check if frame is too short for ETABS
    if (length < MIN_FRAME_LENGTH_TOLERANCE)
    {
      var message = $"Frame too short ({length:F6} {modelUnits}) - ETABS requires minimum {MIN_FRAME_LENGTH_TOLERANCE} {modelUnits}. Skipping.";
      SpeckleLog.Logger.Warning("⚠️ {Message}", message);
      throw new ConversionException(message);
    }

    // Add frame with "Default" section property type
    // ETABS requires a section property to be specified
    var success = Model.FrameObj.AddByCoord(x1, y1, z1, x2, y2, z2, ref newFrame, "Default");

    SpeckleLog.Logger.Information("   API result: success={Code}, frame={Name}", success, newFrame);

    if (success != 0)
    {
      throw new ConversionException($"Failed to add new frame (error code: {success})");
    }

    return newFrame;
  }
}
