using System;
using System.Collections.Generic;
using System.Linq;
#if ETABS22
using ETABSv1;
#else
using CSiAPIv1;
#endif
using Objects.Geometry;
using Objects.Structural.CSI.Geometry;
using Objects.Structural.CSI.Properties;
using Objects.Structural.Geometry;
using Objects.Structural.Properties;
using Serilog;
using Speckle.Core.Kits;
using Speckle.Core.Logging;
using Speckle.Core.Models;

namespace Objects.Converter.CSI;

public partial class ConverterCSI
{
  public void UpdateFrame(Element1D element1D, string name, ApplicationObject appObj)
  {
    var end1node = element1D.end1Node?.basePoint ?? element1D.baseLine?.start;
    var end2node = element1D.end2Node?.basePoint ?? element1D.baseLine?.end;

    if (end1node == null || end2node == null)
    {
      throw new ArgumentException($"Frame {element1D.name} does not have valid endpoints {end1node},{end2node}");
    }

    UpdateFrameLocation(name, end1node, end2node, appObj);
    SetFrameElementProperties(element1D, name, appObj.Log);
  }

  public void UpdateFrameLocation(string name, Point p1, Point p2, ApplicationObject appObj)
  {
    string pt1 = "";
    string pt2 = "";
    Model.FrameObj.GetPoints(name, ref pt1, ref pt2);

    // unfortunately this isn't as easy as just changing the coords of the end points of the frame,
    // as those points may be shared by other frames. Need to check if there are other frames using
    // those points and then check the new location of the endpoints to see if there are existing points
    // that could be used.
    var pt1Updated = UpdatePoint(pt1, null, p1);
    var pt2Updated = UpdatePoint(pt2, null, p2);

    int success = 0;
    if (pt1Updated != pt1 || pt2Updated != pt2)
    {
      success = Model.EditFrame.ChangeConnectivity(name, pt1Updated, pt2Updated);

      int numItems = 0;
      int[] objTypes = null;
      string[] objNames = null;
      int[] pointNums = null;
      Model.PointObj.GetConnectivity(pt1, ref numItems, ref objTypes, ref objNames, ref pointNums);
      if (numItems == 0)
      {
        Model.PointObj.DeleteSpecialPoint(pt1);
      }

      Model.PointObj.GetConnectivity(pt2, ref numItems, ref objTypes, ref objNames, ref pointNums);
      if (numItems == 0)
      {
        Model.PointObj.DeleteSpecialPoint(pt2);
      }
    }

    if (success != 0)
    {
      throw new ConversionException("Failed to change frame connectivity");
    }

    string guid = null;
    Model.FrameObj.GetGUID(name, ref guid);
    appObj.Update(status: ApplicationObject.State.Updated, createdIds: new List<string> { guid }, converted: new List<string> { $"Frame{Delimiter}{name}" });
  }

  public void FrameToNative(Element1D element1D, ApplicationObject appObj)
  {
    if (element1D.type == ElementType1D.Link)
    {
      string createdName = LinkToNative((CSIElement1D)element1D, appObj.Log);
      appObj.Update(status: ApplicationObject.State.Created, createdIds: new List<string> { createdName }, converted: new List<string> { $"Link{Delimiter}{createdName}" });
      return;
    }

    if (ElementExistsWithApplicationId(element1D.applicationId, out string name))
    {
      UpdateFrame(element1D, name, appObj);
      return;
    }

    Line baseline = element1D.baseLine;
    string[] properties = Array.Empty<string>();
    int number = 0;
    int success = Model.PropFrame.GetNameList(ref number, ref properties);
    if (success != 0)
    {
      appObj.Update(logItem: "Failed to retrieve frame section properties");
    }

    if (!properties.Contains(element1D.property.name))
    {
      TryConvertProperty1DToNative(element1D.property, appObj.Log);
      Model.PropFrame.GetNameList(ref number, ref properties);
    }

    Point end1node;
    Point end2node;
    if (baseline != null)
    {
      end1node = baseline.start;
      end2node = baseline.end;
    }
    else
    {
      end1node = element1D.end1Node.basePoint;
      end2node = element1D.end2Node.basePoint;
    }

    CreateFrame(end1node, end2node, out var newFrame, out _, appObj);
    SetFrameElementProperties(element1D, newFrame, appObj.Log);
  }

  public void CreateFrame(
    Point p0,
    Point p1,
    out string newFrame,
    out string guid,
    ApplicationObject appObj,
    string type = "Default",
    string nameOverride = null
  )
  {

    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"📐 Attempting to create frame: Type={type}, Name={nameOverride}, P0={p0.ToString()}, P1={p1.ToString()}");


    newFrame = string.Empty;
    guid = string.Empty;

    int success = Model.FrameObj.AddByCoord(
      ScaleToNative(p0.x, p0.units),
      ScaleToNative(p0.y, p0.units),
      ScaleToNative(p0.z, p0.units),
      ScaleToNative(p1.x, p1.units),
      ScaleToNative(p1.y, p1.units),
      ScaleToNative(p1.z, p1.units),
      ref newFrame,
      type
    );

    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"📏 Attempting to create frame between: P0=({p0.x}, {p0.y}, {p0.z}), P1=({p1.x}, {p1.y}, {p1.z})");
    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"🧩 Type: {type}, Name Override: {nameOverride}, AppObj ID: {appObj.applicationId}");


    Model.FrameObj.GetGUID(newFrame, ref guid);

    if (!string.IsNullOrEmpty(nameOverride) && !GetAllFrameNames(Model).Contains(nameOverride))
    {
      Model.FrameObj.ChangeName(newFrame, nameOverride);
      newFrame = nameOverride;
    }

    if (Math.Abs(p0.x - p1.x) < 0.001 &&
        Math.Abs(p0.y - p1.y) < 0.001 &&
        Math.Abs(p0.z - p1.z) < 0.001)
    {
      SpeckleLog.Logger
        .ForContext<ConverterCSI>()
        .Information("⚠️ Start and end points are identical. Skipping frame creation.");
      throw new ConversionException("Cannot create frame: identical points.");
    }



    if (success != 0)
    {
      SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"❌ Failed to create frame. Likely invalid coordinates or unsupported configuration. P0={p0}, P1={p1}");
      throw new ConversionException("Failed to add new frame object at the specified coordinates");
    }

    appObj.Update(
      status: ApplicationObject.State.Created,
      createdIds: new List<string> { guid },
      converted: new List<string> { $"Frame{Delimiter}{newFrame}" }
    );
  }

  public CSIElement1D FrameToSpeckle(string name)
  {
    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"[FrameToSpeckle] Called for '{name}'");

    string units = ModelUnits();

    string pointI = null, pointJ = null;
    _ = Model.FrameObj.GetPoints(name, ref pointI, ref pointJ);
    var pointINode = PointToSpeckle(pointI);
    var pointJNode = PointToSpeckle(pointJ);
    Line speckleLine = units != null
      ? new Line(pointINode.basePoint, pointJNode.basePoint, units)
      : new Line(pointINode.basePoint, pointJNode.basePoint);

    string property = null, SAuto = null;
    Model.FrameObj.GetSection(name, ref property, ref SAuto);
    var speckleProperty = Property1DToSpeckle(property);

#if ETABS
  eFrameDesignOrientation frameDesignOrientation = eFrameDesignOrientation.Null;
  Model.FrameObj.GetDesignOrientation(name, ref frameDesignOrientation);
#else
    eFrameDesignOrientation frameDesignOrientation = eFrameDesignOrientation.Brace;
    if (Math.Abs(pointJNode.basePoint.z - pointINode.basePoint.z) < 0.001)
      frameDesignOrientation = eFrameDesignOrientation.Beam;
    else if (
      Math.Abs(pointJNode.basePoint.x - pointINode.basePoint.x) < 0.001 &&
      Math.Abs(pointJNode.basePoint.y - pointINode.basePoint.y) < 0.001)
      frameDesignOrientation = eFrameDesignOrientation.Column;
#endif

    var elementType = FrameDesignOrientationToElement1dType(frameDesignOrientation);
    var speckleStructFrame = new CSIElement1D(speckleLine, speckleProperty, elementType)
    {
      name = name,
      end1Node = pointINode,
      end2Node = pointJNode
    };

    bool[] iRelease = null, jRelease = null;
    double[] startV = null, endV = null;
    Model.FrameObj.GetReleases(name, ref iRelease, ref jRelease, ref startV, ref endV);

    speckleStructFrame.end1Releases = RestraintToSpeckle(iRelease);
    speckleStructFrame.end2Releases = RestraintToSpeckle(jRelease);
    SpeckleModel.restraints.Add(speckleStructFrame.end1Releases);
    SpeckleModel.restraints.Add(speckleStructFrame.end2Releases);

    double localAxis = 0;
    bool advanced = false;
    Model.FrameObj.GetLocalAxes(name, ref localAxis, ref advanced);
    speckleStructFrame.orientationAngle = localAxis;

    double offSetEnd1 = 0, offSetEnd2 = 0, RZ = 0;
    bool autoOffSet = true;
    Model.FrameObj.GetEndLengthOffset(name, ref autoOffSet, ref offSetEnd1, ref offSetEnd2, ref RZ);
    speckleStructFrame.end1Offset = new Vector(0, 0, offSetEnd1, ModelUnits());
    speckleStructFrame.end2Offset = new Vector(0, 0, offSetEnd2, ModelUnits());

    string springLineName = null;
    Model.FrameObj.GetSpringAssignment(name, ref springLineName);
    if (springLineName != null)
      speckleStructFrame.CSILinearSpring = LinearSpringToSpeckle(springLineName);

    string pierAssignment = null;
    Model.FrameObj.GetPier(name, ref pierAssignment);
    if (pierAssignment != null)
      speckleStructFrame.PierAssignment = pierAssignment;

    string spandrelAssignment = null;
    Model.FrameObj.GetSpandrel(name, ref spandrelAssignment);
    if (spandrelAssignment != null)
      speckleStructFrame.SpandrelAssignment = spandrelAssignment;

    int designProcedure = 9;
    Model.FrameObj.GetDesignProcedure(name, ref designProcedure);
    if (designProcedure != 9)
    {
      speckleStructFrame.DesignProcedure = designProcedure switch
      {
        0 => DesignProcedure.ProgramDetermined,
        1 => DesignProcedure.SteelFrameDesign,
        2 => DesignProcedure.ConcreteFrameDesign,
        3 => DesignProcedure.CompositeBeamDesign,
        4 => DesignProcedure.SteelJoistDesign,
        7 => DesignProcedure.NoDesign,
        13 => DesignProcedure.CompositeColumnDesign,
        _ => speckleStructFrame.DesignProcedure
      };
    }

    double[] modifiers = Array.Empty<double>();
    if (Model.FrameObj.GetModifiers(name, ref modifiers) == 0)
      speckleStructFrame.StiffnessModifiers = modifiers.ToList();

    speckleStructFrame.AnalysisResults =
      resultsConverter?.Element1DAnalyticalResultConverter?.AnalyticalResultsToSpeckle(
        speckleStructFrame.name,
        speckleStructFrame.type);

    string GUID = "";
    Model.FrameObj.GetGUID(name, ref GUID);
    speckleStructFrame.applicationId = GUID;

    var applicationIds = SpeckleModel.elements.Select(o => o.applicationId).ToList();
    if (!applicationIds.Contains(speckleStructFrame.applicationId))
      SpeckleModel.elements.Add(speckleStructFrame);

    // ✅ Generate Display Mesh
    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"[FrameToSpeckle] Attempting to create extruded mesh for frame '{name}'");

    int cardinalPoint = 8;
    bool mirror2 = false;
    bool stiffTransform = false;
    double[] offset1 = new double[3];
    double[] offset2 = new double[3];
    string cSys = "";

    Model.FrameObj.GetInsertionPoint(
        name,
        ref cardinalPoint,
        ref mirror2,
        ref stiffTransform,
        ref offset1,
        ref offset2,
        ref cSys
    );

    SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"[FrameToSpeckle] Insertion Point: Cardinal = {cardinalPoint}, Mirror2 = {mirror2}, StiffTransform = {stiffTransform}, CSys = {cSys}, Offset1 = [{string.Join(", ", offset1)}], Offset2 = [{string.Join(", ", offset2)}]");

    
    if (sendExtrudedGeometry)
    {
      var extrudedMesh = CreateExtrudedMeshFromFrame(speckleLine, speckleProperty, elementType, cardinalPoint);
      if (extrudedMesh != null)
      {
        speckleStructFrame.displayValue = new List<Base> { extrudedMesh };
        SpeckleLog.Logger
    .ForContext<ConverterCSI>()
    .Information($"[FrameToSpeckle] Successfully added extruded mesh to frame '{name}' displayValue.");
      }
      else
      {
        Log.Warning($"[FrameToSpeckle] Extruded mesh creation returned null for frame '{name}'.");
      }
    }

    else
    {
      SpeckleLog.Logger.Information("[FrameToSpeckle] Extruded geometry disabled — skipping displayValue mesh.");
    }

      SpeckleLog.Logger
    .ForContext<ConverterCSI>()
    .Information($"[FrameToSpeckle] Final displayValue count for '{name}': {speckleStructFrame.displayValue?.Count ?? 0}");

    return speckleStructFrame;
  }


  private static ElementType1D FrameDesignOrientationToElement1dType(eFrameDesignOrientation frameDesignOrientation) =>
    frameDesignOrientation switch
    {
      eFrameDesignOrientation.Column => ElementType1D.Column,
      eFrameDesignOrientation.Beam => ElementType1D.Beam,
      eFrameDesignOrientation.Brace => ElementType1D.Brace,
      eFrameDesignOrientation.Null => ElementType1D.Null,
      eFrameDesignOrientation.Other => ElementType1D.Other,
      _ => throw new SpeckleException($"Unrecognized eFrameDesignOrientation value, {frameDesignOrientation}"),
    };

  public void SetFrameElementProperties(Element1D element1D, string newFrame, IList<string>? log)
  {
    bool[] end1Release = null;
    bool[] end2Release = null;
    double[] startV,
      endV;
    startV = null;
    endV = null;
    if (element1D.end1Releases != null && element1D.end2Releases != null)
    {
      end1Release = RestraintToNative(element1D.end1Releases);
      end2Release = RestraintToNative(element1D.end2Releases);
      startV = PartialRestraintToNative(element1D.end1Releases);
      endV = PartialRestraintToNative(element1D.end2Releases);
    }

    var propertyName = TryConvertProperty1DToNative(element1D.property, log);
    if (propertyName != null)
    {
      Model.FrameObj.SetSection(newFrame, propertyName);
    }

    if (element1D.orientationAngle != 0)
    {
      Model.FrameObj.SetLocalAxes(newFrame, element1D.orientationAngle * (180 / Math.PI)); // Convert from radians to degrees
    }
    end1Release = end1Release.Select(b => !b).ToArray();
    end2Release = end2Release.Select(b => !b).ToArray();

    Model.FrameObj.SetReleases(newFrame, ref end1Release, ref end2Release, ref startV, ref endV);
    if (element1D is CSIElement1D)
    {
      var CSIelement1D = (CSIElement1D)element1D;
      if (CSIelement1D.SpandrelAssignment != null)
      {
        Model.FrameObj.SetSpandrel(newFrame, CSIelement1D.SpandrelAssignment);
      }
      if (CSIelement1D.PierAssignment != null)
      {
        Model.FrameObj.SetPier(newFrame, CSIelement1D.PierAssignment);
      }
      if (CSIelement1D.CSILinearSpring != null)
      {
        Model.FrameObj.SetSpringAssignment(newFrame, CSIelement1D.CSILinearSpring.name);
      }

      if (CSIelement1D.StiffnessModifiers != null)
      {
        var modifiers = CSIelement1D.StiffnessModifiers.ToArray();
        Model.FrameObj.SetModifiers(newFrame, ref modifiers);
      }
      if (CSIelement1D.property.material.name != null)
      {
        switch (CSIelement1D.DesignProcedure)
        {
          case DesignProcedure.ProgramDetermined:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 0);
            break;
          case DesignProcedure.CompositeBeamDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 3);
            break;
          case DesignProcedure.CompositeColumnDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 13);
            break;
          case DesignProcedure.SteelFrameDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 1);
            break;
          case DesignProcedure.ConcreteFrameDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 2);
            break;
          case DesignProcedure.SteelJoistDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 4);
            break;
          case DesignProcedure.NoDesign:
            Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 7);
            break;
        }
      }
      else
      {
        Model.FrameObj.SetDesignProcedure(CSIelement1D.name, 0);
      }
    }
  }

  #region Extrude
  public Mesh CreateExtrudedMeshFromFrame(Line centerLine, Property1D section, ElementType1D elementType, int? cardinalPoint = null)
  {
    SpeckleLog.Logger
      .ForContext<ConverterCSI>()
      .Information($"[CreateExtrudedMeshFromFrame] Called for line from ({centerLine.start.x}, {centerLine.start.y}, {centerLine.start.z}) to ({centerLine.end.x}, {centerLine.end.y}, {centerLine.end.z})");

    double width = 0, depth = 0;

    if (section == null || section.name == null || centerLine == null)
    {
      Log.Warning("[CreateExtrudedMeshFromFrame] Returning null. Reason: section.name or centerLine is null.");
      return null;
    }

    try
    {
      string mat = "", notes = "", guid = "", uniqueName = "";
      int color = 0;
      double t3 = 0, t2 = 0;

      int res = Model.PropFrame.GetRectangle(section.name, ref mat, ref notes, ref t3, ref t2, ref color, ref guid, ref uniqueName);

      if (res == 0)
      {
        depth = t3;
        width = t2;
      }
      else
      {
        width = depth = 1;
      }
    }
    catch
    {
      width = depth = 1;
    }

    var start = centerLine.start;
    var end = centerLine.end;

    Vector dir = new Vector(end.x - start.x, end.y - start.y, end.z - start.z, centerLine.units);
    double length = Math.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
    dir.Normalize();

    Vector up = new Vector(0, 0, 1, centerLine.units);
    Vector right;

    bool isVertical = Math.Abs(dir.x) < 0.001 && Math.Abs(dir.y) < 0.001;

    if (isVertical)
      right = new Vector(1, 0, 0, centerLine.units);
    else
    {
      right = Vector.CrossProduct(dir, up);
      if (Math.Sqrt(right.x * right.x + right.y * right.y + right.z * right.z) < 0.0001)
        right = new Vector(1, 0, 0, centerLine.units);
      else
        right.Normalize();
    }

    Vector upCorrected = Vector.CrossProduct(right, dir);
    upCorrected.Normalize();

    double hw = width / 2;
    double hd = depth / 2;

    Vector downwardOffset = new Vector(0, 0, 0, centerLine.units);

    if (elementType == ElementType1D.Beam)
    {
      string verticalAlign = cardinalPoint.HasValue
        ? GetVerticalAlignmentFromCardinalPoint(cardinalPoint.Value).ToLower()
        : "middle";

      switch (verticalAlign)
      {
        case "bottom":
          downwardOffset = new Vector(0, 0, +depth / 2, centerLine.units);
          break;
        case "middle":
        case "center":
          downwardOffset = new Vector(0, 0, 0, centerLine.units);
          break;
        case "top":
          downwardOffset = new Vector(0, 0, -depth / 2, centerLine.units);
          break;
      }
    }

    var p1 = new Point(start.x + (-hw * right.x + hd * upCorrected.x) + downwardOffset.x,
                       start.y + (-hw * right.y + hd * upCorrected.y) + downwardOffset.y,
                       start.z + (-hw * right.z + hd * upCorrected.z) + downwardOffset.z,
                       centerLine.units);
    var p2 = new Point(start.x + (hw * right.x + hd * upCorrected.x) + downwardOffset.x,
                       start.y + (hw * right.y + hd * upCorrected.y) + downwardOffset.y,
                       start.z + (hw * right.z + hd * upCorrected.z) + downwardOffset.z,
                       centerLine.units);
    var p3 = new Point(start.x + (hw * right.x - hd * upCorrected.x) + downwardOffset.x,
                       start.y + (hw * right.y - hd * upCorrected.y) + downwardOffset.y,
                       start.z + (hw * right.z - hd * upCorrected.z) + downwardOffset.z,
                       centerLine.units);
    var p4 = new Point(start.x + (-hw * right.x - hd * upCorrected.x) + downwardOffset.x,
                       start.y + (-hw * right.y - hd * upCorrected.y) + downwardOffset.y,
                       start.z + (-hw * right.z - hd * upCorrected.z) + downwardOffset.z,
                       centerLine.units);

    var p5 = new Point(p1.x + dir.x * length, p1.y + dir.y * length, p1.z + dir.z * length, centerLine.units);
    var p6 = new Point(p2.x + dir.x * length, p2.y + dir.y * length, p2.z + dir.z * length, centerLine.units);
    var p7 = new Point(p3.x + dir.x * length, p3.y + dir.y * length, p3.z + dir.z * length, centerLine.units);
    var p8 = new Point(p4.x + dir.x * length, p4.y + dir.y * length, p4.z + dir.z * length, centerLine.units);

    var mesh = new Mesh();
    var pts = new List<Point> { p1, p2, p3, p4, p5, p6, p7, p8 };
    foreach (var pt in pts)
      mesh.vertices.AddRange(new List<double> { pt.x, pt.y, pt.z });

    mesh.faces = new List<int>
  {
    4, 0,1,2,3, // bottom
    4, 4,5,6,7, // top
    4, 0,1,5,4,
    4, 1,2,6,5,
    4, 2,3,7,6,
    4, 3,0,4,7
  };

    mesh["debug_tag"] = "extrusion_dynamic";
    mesh.units = centerLine.units;

    return mesh;
  }


  private string GetVerticalAlignmentFromCardinalPoint(int cp)
  {
    return cp switch
    {
      1 or 2 or 3 => "Bottom",
      4 or 5 or 6 => "Middle",
      7 or 8 or 9 => "Top",
      _ => "Middle"
    };
  }
}
#endregion
