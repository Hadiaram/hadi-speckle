using System;
using System.Collections.Generic;
using System.Linq;
using ConverterCSIShared.Extensions;
using NetTopologySuite.Triangulate;
using Objects.Geometry;
using Objects.Structural.CSI.Geometry;
using Objects.Structural.CSI.Properties;
using Objects.Structural.Geometry;
using Objects.Structural.Properties;
using Speckle.Core.Kits;
using Speckle.Core.Logging;
using Speckle.Core.Models;
using StructuralUtilities.PolygonMesher;
using NTS = NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;
using Serilog.Core;


namespace Objects.Converter.CSI;

public partial class ConverterCSI
{
  public object updateExistingArea(Element2D area)
  {
    string GUID = "";
    Model.AreaObj.GetGUID(area.name, ref GUID);
    if (area.applicationId == GUID)
    {
      SetAreaProperties(area.name, area);
    }
    return area.name;
  }

  public void UpdateArea(Element2D area, string name, ApplicationObject appObj)
  {
    var numPoints = 0;
    var points = Array.Empty<string>();
    int success = Model.AreaObj.GetPoints(name, ref numPoints, ref points);
    if (success != 0)
    {
      throw new ConversionException($"Failed to retrieve the names of the point object that define area: {name}");
    }

    bool connectivityChanged = points.Length != area.topology.Count;

    var pointsUpdated = new List<string>();
    for (int i = 0; i < area.topology.Count; i++)
    {
      if (i >= points.Length)
      {
        CreatePoint(area.topology[i].basePoint, out string pointName);
        pointsUpdated.Add(pointName);
        continue;
      }

      pointsUpdated.Add(UpdatePoint(points[i], area.topology[i]));
      if (!connectivityChanged && pointsUpdated[i] != points[i])
      {
        connectivityChanged = true;
      }
    }

    int numErrorMsgs = 0;
    string importLog = "";
    if (connectivityChanged)
    {
#if SAP2000
      var refArray = pointsUpdated.ToArray();
      success = Model.EditArea.ChangeConnectivity(name, pointsUpdated.Count, ref refArray);
      if (success != 0)
      {
        throw new ConversionException($"Failed to modify the connectivity of the area: {name}");
      }
#else
      int tableVersion = 0;
      int numberRecords = 0;
      string[] fieldsKeysIncluded = null;
      string[] tableData = null;
      const string floorTableKey = "Floor Object Connectivity";
      success = Model.DatabaseTables.GetTableForEditingArray(
        floorTableKey,
        "ThisParamIsNotActiveYet",
        ref tableVersion,
        ref fieldsKeysIncluded,
        ref numberRecords,
        ref tableData
      );
      if (success != 0)
      {
        throw new ConversionException($"Failed to retrieve database table for editing table key: {floorTableKey}");
      }

      // if the floor object now has more points than it previously had
      // and it has more points that any other floor object, then updating would involve adding a new column to this array
      // for the moment, forget that, it feels a bit fragile. Just delete the current object and remake it with the same GUID
      if (pointsUpdated.Count > points.Length && pointsUpdated.Count > fieldsKeysIncluded.Length - 2)
      {
        string GUID = "";
        success = Model.AreaObj.GetGUID(name, ref GUID);
        if (success != 0)
        {
          throw new ConversionException($"Failed to retrieve the GUID for area: {name}");
        }

        var updatedArea = AreaToSpeckle(name);

        updatedArea.applicationId = GUID;
        updatedArea.topology = area.topology;

        Model.AreaObj.Delete(name);
        ExistingObjectGuids.Remove(GUID);
        var dummyAppObj = new ApplicationObject(null, null);
        AreaToNative(updatedArea, dummyAppObj);
        if (dummyAppObj.Status != ApplicationObject.State.Created)
        {
          throw new SpeckleException("Area failed!"); //This should never happen, AreaToNative should throw
        }
      }
      else
      {
        for (int record = 0; record < numberRecords; record++)
        {
          if (tableData[record * fieldsKeysIncluded.Length] != name)
          {
            continue;
          }

          for (int i = 0; i < pointsUpdated.Count; i++)
          {
            tableData[record * fieldsKeysIncluded.Length + (i + 1)] = pointsUpdated[i];
          }

          break;
        }

        // this is a workaround for a CSI bug. The applyEditedTables is looking for "Unique Name", not "UniqueName"
        // this bug is patched in version 20.0.0
        if (ProgramVersion.CompareTo("20.0.0") < 0 && fieldsKeysIncluded[0] == "UniqueName")
        {
          fieldsKeysIncluded[0] = "Unique Name";
        }

        Model.DatabaseTables.SetTableForEditingArray(
          floorTableKey,
          ref tableVersion,
          ref fieldsKeysIncluded,
          numberRecords,
          ref tableData
        );

        int numFatalErrors = 0;
        int numWarnMsgs = 0;
        int numInfoMsgs = 0;
        success = Model.DatabaseTables.ApplyEditedTables(
          true,
          ref numFatalErrors,
          ref numErrorMsgs,
          ref numWarnMsgs,
          ref numInfoMsgs,
          ref importLog
        );

        if (success != 0)
        {
          appObj.Log.Add(importLog);
          throw new ConversionException("Failed to apply edited database tables");
        }

        int numItems = 0;
        int[] objTypes = null;
        string[] objNames = null;
        int[] pointNums = null;
        foreach (var node in points)
        {
          Model.PointObj.GetConnectivity(node, ref numItems, ref objTypes, ref objNames, ref pointNums);
          if (numItems == 0)
          {
            Model.PointObj.DeleteSpecialPoint(node);
          }
        }
      }
#endif
    }

    SetAreaProperties(name, area);

    string guid = null;
    Model.AreaObj.GetGUID(name, ref guid);

    appObj.Update(status: ApplicationObject.State.Updated, createdIds: new List<string> { guid }, converted: new List<string> { $"Area{Delimiter}{name}" });

    if (numErrorMsgs != 0)
    {
      appObj.Update(
        log: new List<string>()
        {
          $"Area may not have updated successfully. Number of error messages for operation is {numErrorMsgs}",
          importLog
        }
      );
    }
  }

  public void AreaToNative(Element2D area, ApplicationObject appObj)
  {
    // ✅ Step 1: Detect and handle opening based on debug tag
    if (area.displayValue?.Any(m => m["debug_tag"]?.ToString() == "Opening") == true)
    {
      if (InjectOpeningIntoSlab(area, out string openingName))
      {
        string openingGuid = "";
        if (!string.IsNullOrEmpty(area.applicationId))
        {
          openingGuid = area.applicationId;
        }
        else
        {
          Model.AreaObj.GetGUID(openingName, ref openingGuid);
        }
        appObj.Update(status: ApplicationObject.State.Created, createdIds: new List<string> { openingGuid }, converted: new List<string> { $"Opening{Delimiter}{openingName}" }, logItem: $"✅ Opening {openingName} created and flagged.");
      }
      else
      {
        appObj.Update(status: ApplicationObject.State.Failed, logItem: "❌ Failed to create and flag opening.");
      }
      return; // skip further slab logic
    }

    // === Step 2: Default slab handling ===

    if (ElementExistsWithApplicationId(area.applicationId, out string areaName))
    {
      UpdateArea(area, areaName, appObj);
      return;
    }

    if (GetAllAreaNames(Model).Contains(area.name))
    {
      area.name = $"{area.name}_{Guid.NewGuid().ToString().Substring(0, 4)}";
    }

    var propName = CreateOrGetProp(area.property, out bool isPropertyHandled);
    if (!isPropertyHandled)
    {
      appObj.Update(
        logItem: $"Area section for object could not be created and was replaced with section named \"{propName}\""
      );
    }

    var name = CreateAreaFromPoints(area.topology.Select(t => t.basePoint), propName);
    SetAreaProperties(name, area);

    if (!string.IsNullOrEmpty(area.applicationId))
    {
      Model.AreaObj.SetGUID(name, area.applicationId);
    }

    var guid = "";
    Model.AreaObj.GetGUID(name, ref guid);

    appObj.Update(status: ApplicationObject.State.Created, createdIds: new List<string> { guid }, converted: new List<string> { $"Area{Delimiter}{name}" });

    SpeckleLog.Logger.Information($"[AreaToNative] 🔍 Received Element2D: {area.name}, applicationId: {area.applicationId}");

    if (area.displayValue == null)
      SpeckleLog.Logger.Warning($"[AreaToNative] ⚠ displayValue is null for {area.name}");
    else
    {
      foreach (var mesh in area.displayValue)
        SpeckleLog.Logger.Warning($"[AreaToNative] 🔍 Mesh debug_tag: {mesh["debug_tag"]}");
    }

  }

  private string CreateAreaFromPoints(IEnumerable<Point> points, string propName)
  {
    var name = "";
    int numPoints = 0;
    List<double> X = new();
    List<double> Y = new();
    List<double> Z = new();

    foreach (var point in points)
    {
      X.Add(ScaleToNative(point.x, point.units));
      Y.Add(ScaleToNative(point.y, point.units));
      Z.Add(ScaleToNative(point.z, point.units));
      numPoints++;
    }

    // Remove last if it's a duplicate of the first
    if (
      Math.Abs(X.Last() - X.First()) < .01
      && Math.Abs(Y.Last() - Y.First()) < .01
      && Math.Abs(Z.Last() - Z.First()) < .01
    )
    {
      X.RemoveAt(X.Count - 1);
      Y.RemoveAt(Y.Count - 1);
      Z.RemoveAt(Z.Count - 1);
      numPoints--;
    }

    var x = X.ToArray();
    var y = Y.ToArray();
    var z = Z.ToArray();

    SpeckleLog.Logger.Information("📐 Creating area with {Count} points and prop '{Prop}'", numPoints, propName);
    SpeckleLog.Logger.Information("🔧 X: {0}", string.Join(", ", x));
    SpeckleLog.Logger.Information("🔧 Y: {0}", string.Join(", ", y));
    SpeckleLog.Logger.Information("🔧 Z: {0}", string.Join(", ", z));

    int success = Model.AreaObj.AddByCoord(numPoints, ref x, ref y, ref z, ref name, propName);

    if (success != 0)
    {
      throw new ConversionException($"❌ Failed to add new area object for area {name} at coords: X={string.Join(",", x)} Y={string.Join(",", y)} Z={string.Join(",", z)}");
    }

    SpeckleLog.Logger.Information("✅ Created area named {Name}", name);

    return name;
  }


  private string? CreateOrGetProp(Property2D property, out bool isPropertyHandled)
  {
    int numberNames = 0;
    string[] propNames = Array.Empty<string>();

    int success = Model.PropArea.GetNameList(ref numberNames, ref propNames);
    if (success != 0)
    {
      throw new ConversionException("Failed to retrieve the names of all defined area properties");
    }

    isPropertyHandled = true;

    // Use the property if it already exists in the analytical model
    if (propNames.Contains(property?.name))
    {
      return property.name;
    }

    // Create detailed property if it is of type CSI and doesn't exist in the analytical model
    if (property is CSIProperty2D csiProp2D)
    {
      try
      {
        return Property2DToNative(csiProp2D);
      }
      catch (Exception ex) when (!ex.IsFatal())
      {
        SpeckleLog.Logger.Error(ex, "Unable to create property2d");
        isPropertyHandled = false;
      }
    }

    /* Create the property with information we can use if it is a Property2D (i.e. thickness)
    * Furthermore, a property can only be created if it has a name (hence the two conditionals).
    * Name is inherited from the physical association in Revit (i.e. it comes from the type name of the family) */
    if (property is Property2D structuralProp2D && !string.IsNullOrEmpty(structuralProp2D.name))
    {
      try
      {
        return Property2DToNative(structuralProp2D);
      }
      catch (Exception ex) when (!ex.IsFatal())
      {
        SpeckleLog.Logger.Error(ex, "Unable to create property2d");
      }
    }
    isPropertyHandled = false;
    if (propNames.Length > 0)
    {
      return propNames.First();
    }

    throw new ConversionException(
      "Cannot create area because there aren't any area sections defined in the project file"
    );
  }

  public void SetAreaProperties(string name, Element2D area)
  {
    if (!string.IsNullOrEmpty(area.name))
    {
      if (GetAllAreaNames(Model).Contains(area.name))
      {
        area.name = area.id;
      }

      Model.AreaObj.ChangeName(name, area.name);
      name = area.name;
    }

    Model.AreaObj.SetProperty(name, area.property?.name);
    if (area is CSIElement2D csiArea)
    {
      double[] values = null;
      if (csiArea.StiffnessModifiers != null)
      {
        values = csiArea.StiffnessModifiers.ToArray();
      }

      Model.AreaObj.SetModifiers(name, ref values);
      Model.AreaObj.SetLocalAxes(name, csiArea.orientationAngle);
      Model.AreaObj.SetPier(name, csiArea.PierAssignment);
      Model.AreaObj.SetSpandrel(name, csiArea.SpandrelAssignment);
      if (csiArea.CSIAreaSpring != null)
      {
        Model.AreaObj.SetSpringAssignment(name, csiArea.CSIAreaSpring.name);
      }

      // ⚠ Skip diaphragm for openings
      if (csiArea.DiaphragmAssignment != null && csiArea.property is not CSIOpening)
      {
        try
        {
          Model.AreaObj.SetDiaphragm(name, csiArea.DiaphragmAssignment);
        }
        catch (Exception ex)
        {
          SpeckleLog.Logger.Warning(ex, $"[SetAreaProperties] ⚠ Failed to assign diaphragm {csiArea.DiaphragmAssignment} to area {name}");
        }
      }

    }
  }

  public Element2D AreaToSpeckle(string name)
  {
    string units = ModelUnits();
    var speckleStructArea = new CSIElement2D();

    speckleStructArea.name = name;
    int numPoints = 0;
    string[] points = null;
    Model.AreaObj.GetPoints(name, ref numPoints, ref points);
    List<Node> nodes = new();
    foreach (string point in points)
    {
      Node node = PointToSpeckle(point);
      node.id = point; // 🟢 This will fix the "Unnamed" issue
      nodes.Add(node);
    }

    speckleStructArea.topology = nodes;

    bool isOpening = false;
    Model.AreaObj.GetOpening(name, ref isOpening);
    if (isOpening)
    {
      var guid = "";
      Model.AreaObj.GetGUID(name, ref guid);

      // Avoid variable name conflict
      int openingNumPoints = 0;
      string[] openingPointNames = null;
      Model.AreaObj.GetPoints(name, ref openingNumPoints, ref openingPointNames);

      List<Node> openingNodes = new();
      foreach (string pt in openingPointNames)
      {
        Node node = PointToSpeckle(pt);
        node.id = pt;
        openingNodes.Add(node);
      }

      List<Point> openingPts = openingNodes.Select(n => n.basePoint).ToList();
      string openingUnits = openingPts.FirstOrDefault()?.units ?? "m";

      // Build flat mesh at slab surface
      var mesh = new Mesh();
      foreach (var pt in openingPts)
        mesh.vertices.AddRange(new[] { pt.x, pt.y, pt.z });

      for (int i = 1; i < openingPts.Count - 1; i++)
      {
        mesh.faces.Add(0);
        mesh.faces.Add(0);
        mesh.faces.Add(i);
        mesh.faces.Add(i + 1);
      }

      int red = unchecked((int)0x00FFFFFF);
      mesh.colors = Enumerable.Repeat(red, openingPts.Count).ToList();
      mesh.units = openingUnits;
      mesh["debug_tag"] = "flat_opening_indicator";
      mesh["debug_tag"] = "Opening";

      var openingElement = new CSIElement2D
      {
        name = name,
        property = new CSIOpening(true),
        applicationId = guid,
        topology = openingNodes,
        displayValue = new List<Mesh> { mesh }
      };

      openingElement["elements"] = CreateOpeningCrossLines(openingNodes);

      return openingElement;
    }

    else
    {
      string propName = "";
      Model.AreaObj.GetProperty(name, ref propName);
      speckleStructArea.property = Property2DToSpeckle(name, propName);
    }

    List<double> coordinates = new() { };
    foreach (Node node in nodes)
    {
      coordinates.Add(node.basePoint.x);
      coordinates.Add(node.basePoint.y);
      coordinates.Add(node.basePoint.z);
    }

    //Get orientation angle
    double angle = 0;
    bool advanced = true;
    Model.AreaObj.GetLocalAxes(name, ref angle, ref advanced);
    speckleStructArea.orientationAngle = angle;
    if (coordinates.Count != 0)
    {
      PolygonMesher polygonMesher = new();
      polygonMesher.Init(coordinates);
      var faces = polygonMesher.Faces();
      var vertices = polygonMesher.Coordinates;
      //speckleStructArea.displayMesh = new Geometry.Mesh(vertices, faces.ToArray(), null, null, ModelUnits(), null);
      speckleStructArea.displayValue = new List<Geometry.Mesh> { new(vertices.ToList(), faces, units: ModelUnits()) };
    }

    //Model.AreaObj.GetModifiers(area, ref value);
    //speckleProperty2D.modifierInPlane = value[2];
    //speckleProperty2D.modifierBending = value[5];
    //speckleProperty2D.modifierShear = value[6];

    double[] values = null;
    Model.AreaObj.GetModifiers(name, ref values);
    speckleStructArea.StiffnessModifiers = values.ToList();

    string springArea = null;
    Model.AreaObj.GetSpringAssignment(name, ref springArea);
    if (springArea != null)
    {
      speckleStructArea.CSIAreaSpring = AreaSpringToSpeckle(springArea);
    }

    string pierAssignment = null;
    Model.AreaObj.GetPier(name, ref pierAssignment);
    if (pierAssignment != null)
    {
      speckleStructArea.PierAssignment = pierAssignment;
    }

    string spandrelAssignment = null;
    Model.AreaObj.GetSpandrel(name, ref spandrelAssignment);
    if (spandrelAssignment != null)
    {
      speckleStructArea.SpandrelAssignment = spandrelAssignment;
    }

    string diaphragmAssignment = null;
    Model.AreaObj.GetDiaphragm(name, ref diaphragmAssignment);
    if (diaphragmAssignment != null)
    {
      speckleStructArea.DiaphragmAssignment = diaphragmAssignment;
    }

    speckleStructArea.AnalysisResults =
      resultsConverter?.Element2DAnalyticalResultConverter?.AnalyticalResultsToSpeckle(speckleStructArea.name);

    var GUID = "";
    Model.AreaObj.GetGUID(name, ref GUID);
    speckleStructArea.applicationId = GUID;

    if (sendExtrudedGeometry)
    {
      // ✅ Attempt to add extruded slab mesh
      try
      {
        double thickness = 0.1;
        if (speckleStructArea.property is Property2D p2d && p2d.thickness > 0)
          thickness = p2d.thickness;

        var extrudedMesh = CreateExtrudedMeshFromSurface(new List<List<Node>> { speckleStructArea.topology }, thickness);
        bool isFallback = extrudedMesh?["debug_tag"]?.ToString()?.Contains("fallback") ?? true;

        if (extrudedMesh != null && !isFallback)
        {
          speckleStructArea.displayValue = new List<Mesh> { extrudedMesh };
          SpeckleLog.Logger.Information($"[AreaToSpeckle] ✅ Extruded mesh added with {extrudedMesh.vertices.Count / 3} vertices");
        }
        else
        {
          SpeckleLog.Logger.Warning("[AreaToSpeckle] ⛔ Extrusion failed or fallback mesh used — defaulting to 2D surface only");

          var baseMesh = speckleStructArea.displayValue?.FirstOrDefault();
          if (baseMesh != null)
          {
            var bottomMesh = TranslateMesh(baseMesh, thickness);
            if (bottomMesh != null)
            {
              var shellMesh = ConnectTopAndBottom(baseMesh, bottomMesh);
              speckleStructArea.displayValue.Add(shellMesh);
              SpeckleLog.Logger.Information("[AreaToSpeckle] ✅ Added translated 2D bottom mesh (fake extrusion)");
            }
          }
        }
      }
      catch (Exception ex)
      {
        SpeckleLog.Logger.Warning($"[AreaToSpeckle] ⛔ Exception in extrusion: {ex.Message} — reverting to flat polygon");
      }
    }

  else
    {
      SpeckleLog.Logger.Information("[AreaToSpeckle] Reverting to 2D View");
    }


      IList<Base> elements = SpeckleModel == null ? Array.Empty<Base>() : SpeckleModel.elements;
    var applicationId = elements.Select(o => o.applicationId);
    if (!applicationId.Contains(speckleStructArea.applicationId))
    {
      SpeckleModel?.elements.Add(speckleStructArea);
    }

    // Should discretize between wall and slab types
    //speckleStructArea.memberType = MemberType.Generic2D;

    LogResolvedLoadSetForArea(name);

    if (areaToLoadSet.TryGetValue(name, out string loadSet))
    {
      var resolved = allResolvedRows
          .Where(row => row.LoadSet == loadSet)
          .ToList();

      var serializableRows = resolved.Select(r => new SerializableResolvedLoadRow
      {
        UniqueName = r.Name,
        LoadPattern = r.LoadPattern,
        Value = r.LoadValue,
      }).ToList();

      speckleStructArea["loadSetData"] = new LoadSetData(loadSet, serializableRows);
    }



    return speckleStructArea;
  }

  #region Extrude
  public Mesh CreateExtrudedMeshFromSurface(List<List<Node>> loops, double thickness, string debugTag = "extruded_debugged")
  {
    if (loops == null || loops.Count == 0 || loops[0].Count < 3)
      return null;

    string units = loops[0][0].basePoint.units ?? "m";
    var outer = loops[0].Select(n => n.basePoint).ToList();

    // === Compute normal vector ===
    Vector v1 = new Vector(outer[1].x - outer[0].x, outer[1].y - outer[0].y, outer[1].z - outer[0].z, units);
    Vector v2 = new Vector(outer[2].x - outer[0].x, outer[2].y - outer[0].y, outer[2].z - outer[0].z, units);
    Vector normal = Vector.CrossProduct(v1, v2);

    double mag = Math.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
    if (mag < 1e-6) return null;
    normal.x /= mag; normal.y /= mag; normal.z /= mag;

    bool isVertical = Math.Abs(normal.z) < 0.3;
    SpeckleLog.Logger.Information("[CreateExtrudedMeshFromSurface] Detected {0} orientation (normal = {1:F2}, {2:F2}, {3:F2})",
      isVertical ? "vertical" : "horizontal", normal.x, normal.y, normal.z);

    var mesh = new Mesh();
    List<int> loopVertexCounts = new();
    List<List<Point>> topLoops = new();
    List<List<Point>> bottomLoops = new();

    for (int i = 0; i < loops.Count; i++)
    {
      var loop = loops[i];
      var top = loop.Select(n =>
      {
        var pt = n.basePoint;
        pt["id"] = n.id;
        return pt;
      }).ToList();

      var bottom = top.Select(p => new Point(
        p.x - normal.x * thickness,
        p.y - normal.y * thickness,
        p.z - normal.z * thickness,
        units)
      { ["id"] = p["id"] }).ToList();

      topLoops.Add(top);
      bottomLoops.Add(bottom);
      loopVertexCounts.Add(top.Count);
    }

    foreach (var loop in topLoops)
      foreach (var pt in loop)
        mesh.vertices.AddRange(new[] { pt.x, pt.y, pt.z });

    foreach (var loop in bottomLoops)
      foreach (var pt in loop)
        mesh.vertices.AddRange(new[] { pt.x, pt.y, pt.z });

    int offset = loopVertexCounts.Sum();

    // === Top face ===
    var topOuter = topLoops[0];
    if (IsClosedLoop(topOuter)) topOuter.RemoveAt(topOuter.Count - 1);
    var topFaceLoops = new List<List<Point>> { topOuter };
    for (int i = 1; i < topLoops.Count; i++)
    {
      var loop = topLoops[i];
      if (IsClosedLoop(loop)) loop.RemoveAt(loop.Count - 1);
      topFaceLoops.Add(loop);
    }

    List<int[]> topTriangles = isVertical
      ? TriangulateFan(Enumerable.Range(0, topOuter.Count).ToList())
      : TriangulateWithHoles(topFaceLoops, 0);

    if (topTriangles.Count == 0)
    {
      SpeckleLog.Logger.Warning("[Fallback] Triangulation failed — using wrapper fallback method.");
      return CreateWrappedMeshFromLoops(topLoops, bottomLoops, units);
    }

    foreach (var tri in topTriangles)
    {
      mesh.faces.Add(0);
      mesh.faces.AddRange(tri);
    }

    // === Bottom face ===
    var bottomOuter = bottomLoops[0];
    if (IsClosedLoop(bottomOuter)) bottomOuter.RemoveAt(bottomOuter.Count - 1);
    var bottomFaceLoops = new List<List<Point>> { bottomOuter };
    for (int i = 1; i < bottomLoops.Count; i++)
    {
      var loop = bottomLoops[i];
      if (IsClosedLoop(loop)) loop.RemoveAt(loop.Count - 1);
      bottomFaceLoops.Add(loop);
    }

    List<int[]> bottomTriangles = isVertical
      ? TriangulateFan(Enumerable.Range(0, bottomOuter.Count).Select(i => i + offset).ToList())
      : TriangulateWithHoles(bottomFaceLoops, offset);

    if (bottomTriangles.Count == 0)
    {
      SpeckleLog.Logger.Warning("[Fallback] Triangulation failed — using wrapper fallback method.");
      return CreateWrappedMeshFromLoops(topLoops, bottomLoops, units);
    }

    foreach (var tri in bottomTriangles)
    {
      mesh.faces.Add(0);
      mesh.faces.AddRange(tri);
    }

    // === Side faces ===
    for (int k = 0; k < loops.Count; k++)
    {
      int count = loopVertexCounts[k];
      int topStart = loopVertexCounts.Take(k).Sum();
      int botStart = offset + topStart;

      for (int i = 0; i < count; i++)
      {
        int j = (i + 1) % count;
        mesh.faces.Add(4);
        mesh.faces.Add(topStart + i);
        mesh.faces.Add(topStart + j);
        mesh.faces.Add(botStart + j);
        mesh.faces.Add(botStart + i);
      }
    }

    mesh.units = units;
    mesh["debug_tag"] = debugTag;
    return mesh;
  }

  private List<int[]> SafeTriangulateWithFallback(List<List<Point>> loops, int offset)
  {
    try
    {
      return TriangulateWithHoles(loops, offset);
    }
    catch (Exception ex)
    {
      SpeckleLog.Logger.Warning("[SafeTriangulateWithFallback] ❌ Triangulation failed: {Error}", ex.Message);
      SpeckleLog.Logger.Warning("[SafeTriangulateWithFallback] ⚠️ Patching only outer loop to avoid invalid fill.");

      List<int[]> fallback = new();
      var outer = loops[0];
      for (int i = 1; i < outer.Count - 1; i++)
      {
        fallback.Add(new[] { offset + 0, offset + i, offset + i + 1 });
      }
      return fallback;
    }
  }
  private List<int[]> TriangulateWithHoles(List<List<Point>> loops, int offset)
  {
    if (loops == null || loops.Count == 0)
      return new();

    var outer = loops[0];
    var holes = loops.Skip(1).ToList();

    // Flatten all points into one list for indexing
    List<Point> allPoints = new();
    allPoints.AddRange(outer);
    foreach (var h in holes) allPoints.AddRange(h);

    // Convert outer to shell
    var shellCoords = outer.Select(p => new NTS.Coordinate(p.x, p.y)).ToList();
    if (!shellCoords.First().Equals2D(shellCoords.Last()))
      shellCoords.Add(shellCoords.First());
    var shell = new NTS.LinearRing(shellCoords.ToArray());

    // Convert holes to rings
    var holeRings = new List<NTS.LinearRing>();
    foreach (var hole in holes)
    {
      var holeCoords = hole.Select(p => new NTS.Coordinate(p.x, p.y)).ToList();
      if (!holeCoords.First().Equals2D(holeCoords.Last()))
        holeCoords.Add(holeCoords.First());
      holeRings.Add(new NTS.LinearRing(holeCoords.ToArray()));
    }

    var poly = new NTS.Polygon(shell, holeRings.ToArray());
    var polys = new List<NTS.Polygon> { poly };

    var result = new List<int[]>();
    var gf = new NTS.GeometryFactory();

    foreach (var p in polys)
    {
      var rings = new List<List<Point>>();

      // Exterior ring
      var extRing = p.ExteriorRing.Coordinates.Select(c => new Point(c.X, c.Y, 0, "m")).ToList();
      if (IsClosedLoop(extRing)) extRing.RemoveAt(extRing.Count - 1);
      rings.Add(extRing);

      // Interior rings (holes)
      for (int i = 0; i < p.NumInteriorRings; i++)
      {
        var coords = p.GetInteriorRingN(i).Coordinates.Select(c => new Point(c.X, c.Y, 0, "m")).ToList();
        if (IsClosedLoop(coords)) coords.RemoveAt(coords.Count - 1);
        rings.Add(coords);
      }

      // Triangulate each ring using EarClip
      foreach (var ring in rings)
      {
        var tris = SafeTriangulate(ring, offset);
        result.AddRange(tris);
      }
    }

    return result;
  }

  private List<int[]> TriangulateEarClip(List<Point> points, int offset = 0)
  {
    List<int[]> triangles = new();
    List<int> available = Enumerable.Range(0, points.Count).ToList();

    while (available.Count >= 3)
    {
      bool earFound = false;

      for (int i = 0; i < available.Count; i++)
      {
        int prev = available[(i - 1 + available.Count) % available.Count];
        int curr = available[i];
        int next = available[(i + 1) % available.Count];

        Point pPrev = points[prev];
        Point pCurr = points[curr];
        Point pNext = points[next];

        Vector a = new Vector(pCurr.x - pPrev.x, pCurr.y - pPrev.y, 0, pCurr.units);
        Vector b = new Vector(pNext.x - pCurr.x, pNext.y - pCurr.y, 0, pCurr.units);
        double cross = a.x * b.y - a.y * b.x;

        if (cross <= 0) continue;

        bool hasInner = false;
        for (int j = 0; j < available.Count; j++)
        {
          if (j == (i - 1 + available.Count) % available.Count ||
              j == i ||
              j == (i + 1) % available.Count)
            continue;

          if (PointInTriangle(points[available[j]], pPrev, pCurr, pNext))
          {
            hasInner = true;
            break;
          }
        }

        if (!hasInner)
        {
          triangles.Add(new int[] { prev + offset, curr + offset, next + offset });
          available.RemoveAt(i);
          earFound = true;
          break;
        }
      }

      if (!earFound)
      {
        throw new Exception("Failed to find ear for triangulation. Polygon may be complex or self-intersecting.");
      }
    }

    return triangles;
  }

  private bool IsRectangle(List<Point> pts)
  {
    if (pts.Count != 4) return false;

    for (int i = 0; i < 4; i++)
    {
      var p0 = pts[i];
      var p1 = pts[(i + 1) % 4];
      var p2 = pts[(i + 2) % 4];

      var dx1 = p1.x - p0.x;
      var dy1 = p1.y - p0.y;
      var dx2 = p2.x - p1.x;
      var dy2 = p2.y - p1.y;

      var dot = dx1 * dx2 + dy1 * dy2;
      if (Math.Abs(dot) > 1e-6) return false;
    }

    return true;
  }

  private bool PointInTriangle(Point p, Point a, Point b, Point c)
  {
    double area = 0.5 * (-b.y * c.x + a.y * (-b.x + c.x) + a.x * (b.y - c.y) + b.x * c.y);
    double s = 1 / (2 * area) * (a.y * c.x - a.x * c.y + (c.y - a.y) * p.x + (a.x - c.x) * p.y);
    double t = 1 / (2 * area) * (a.x * b.y - a.y * b.x + (a.y - b.y) * p.x + (b.x - a.x) * p.y);

    return s > 0 && t > 0 && (1 - s - t) > 0;
  }

  private List<int[]> TriangulateFan(List<int> indices)
  {
    var tris = new List<int[]>();
    for (int i = 1; i < indices.Count - 1; i++)
      tris.Add(new[] { indices[0], indices[i], indices[i + 1] });
    return tris;
  }
  private bool IsClosedLoop(List<Point> pts)
  {
    return Math.Abs(pts[0].x - pts[pts.Count - 1].x) < 1e-6 &&
           Math.Abs(pts[0].y - pts[pts.Count - 1].y) < 1e-6 &&
           Math.Abs(pts[0].z - pts[pts.Count - 1].z) < 1e-6;
  }

  private List<int[]> SafeTriangulate(List<Point> points, int offset)
  {
    try
    {
      return TriangulateEarClip(points, offset);
    }
    catch (Exception ex)
    {
      SpeckleLog.Logger.Warning($"[SafeTriangulate] ❌ Triangulation failed: {ex.Message}");
      foreach (var p in points)
        SpeckleLog.Logger.Warning($"[SafeTriangulate] Point: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
      return new List<int[]>();
    }
  }

  public Mesh CreateWrappedMeshFromLoops(List<List<Point>> topLoops, List<List<Point>> bottomLoops, string units)
  {
    var mesh = new Mesh();

    // Add all vertices
    foreach (var loop in topLoops)
      foreach (var pt in loop)
        mesh.vertices.AddRange(new[] { pt.x, pt.y, pt.z });

    foreach (var loop in bottomLoops)
      foreach (var pt in loop)
        mesh.vertices.AddRange(new[] { pt.x, pt.y, pt.z });

    int topOffset = 0;
    int bottomOffset = topLoops.Sum(l => l.Count);

    // === Top Faces (individually triangulate each loop) ===
    for (int i = 0; i < topLoops.Count; i++)
    {
      var loop = topLoops[i];
      var tris = SafeTriangulate(loop, topOffset);
      foreach (var tri in tris)
      {
        mesh.faces.Add(0);
        mesh.faces.AddRange(tri);
      }
      topOffset += loop.Count;
    }

    // === Bottom Faces (reverse each triangle for correct normals) ===
    for (int i = 0; i < bottomLoops.Count; i++)
    {
      var loop = bottomLoops[i];
      var tris = SafeTriangulate(loop, bottomOffset);
      foreach (var tri in tris)
      {
        // Reverse triangle winding order
        mesh.faces.Add(0);
        mesh.faces.Add(tri[0]);
        mesh.faces.Add(tri[2]);
        mesh.faces.Add(tri[1]);
      }
      bottomOffset += loop.Count;
    }

    // === Side Faces ===
    int cumulativeTop = 0;
    int cumulativeBot = topLoops.Sum(l => l.Count);

    for (int k = 0; k < topLoops.Count; k++)
    {
      var topLoop = topLoops[k];
      var bottomLoop = bottomLoops[k];
      int count = topLoop.Count;

      int topStart = cumulativeTop;
      int botStart = cumulativeBot;

      for (int i = 0; i < count; i++)
      {
        int j = (i + 1) % count;

        mesh.faces.Add(4);
        mesh.faces.Add(topStart + i);
        mesh.faces.Add(topStart + j);
        mesh.faces.Add(botStart + j);
        mesh.faces.Add(botStart + i);
      }

      cumulativeTop += topLoop.Count;
      cumulativeBot += bottomLoop.Count;
    }

    mesh.units = units;
    mesh["debug_tag"] = "wrapped_fallback_fixed";
    return mesh;
  }

  public Mesh TranslateMesh(Mesh original, double thickness)
  {
    if (original == null || original.vertices.Count % 3 != 0)
      return null;

    var offsetMesh = new Mesh();
    offsetMesh.units = original.units;

    // Copy vertices downward
    for (int i = 0; i < original.vertices.Count; i += 3)
    {
      double x = original.vertices[i];
      double y = original.vertices[i + 1];
      double z = original.vertices[i + 2] - thickness;

      offsetMesh.vertices.AddRange(new[] { x, y, z });
    }

    // Copy valid faces
    int iFace = 0;
    while (iFace < original.faces.Count)
    {
      int faceType = original.faces[iFace];
      if (faceType == 0 && iFace + 3 < original.faces.Count)
      {
        offsetMesh.faces.AddRange(original.faces.GetRange(iFace, 4));
        iFace += 4;
      }
      else if (faceType == 3 && iFace + 3 < original.faces.Count)
      {
        offsetMesh.faces.AddRange(original.faces.GetRange(iFace, 4)); // 1 (type) + 3 (indices)
        iFace += 4;
      }
      else if (faceType == 4 && iFace + 5 < original.faces.Count)
      {
        offsetMesh.faces.AddRange(original.faces.GetRange(iFace, 5)); // 1 (type) + 4 (indices)
        iFace += 5;
      }
      else
      {
        SpeckleLog.Logger.Warning("[TranslateMesh] ⚠ Unknown face type {0}, skipping.", faceType);
        iFace += faceType + 1;
      }
    }

    SpeckleLog.Logger.Information("[TranslateMesh] Translated mesh has {0} vertices and {1} faces",
        offsetMesh.vertices.Count, offsetMesh.faces.Count);

    // Validate vertices
    for (int i = 0; i < offsetMesh.vertices.Count; i += 3)
    {
      double x = offsetMesh.vertices[i];
      double y = offsetMesh.vertices[i + 1];
      double z = offsetMesh.vertices[i + 2];

      if (double.IsNaN(x) || double.IsInfinity(x) ||
          double.IsNaN(y) || double.IsInfinity(y) ||
          double.IsNaN(z) || double.IsInfinity(z))
      {
        SpeckleLog.Logger.Warning("[TranslateMesh] ⚠ Invalid vertex at index {0}: ({1}, {2}, {3})", i / 3, x, y, z);
      }
    }

    // Validate faces
    for (int i = 0; i < offsetMesh.faces.Count;)
    {
      int faceType = offsetMesh.faces[i];
      if (faceType != 0 && faceType != 3 && faceType != 4)
      {
        SpeckleLog.Logger.Warning("[TranslateMesh] ⚠ Unsupported face type {0} at index {1}", faceType, i);
      }

      i += faceType == 0 || faceType == 3 ? 4 : 5;
    }

    offsetMesh["debug_tag"] = "translated_2d_copy";
    return offsetMesh;
  }

  public Mesh ConnectTopAndBottom(Mesh top, Mesh bottom)
  {
    if (top == null || bottom == null)
    {
      SpeckleLog.Logger.Warning("[ConnectTopAndBottom] ❌ One of the meshes is null.");
      return null;
    }

    if (top.vertices.Count != bottom.vertices.Count)
    {
      SpeckleLog.Logger.Warning($"[ConnectTopAndBottom] ❌ Vertex count mismatch: Top = {top.vertices.Count}, Bottom = {bottom.vertices.Count}");
      return null;
    }

    int numVertices = top.vertices.Count / 3;
    var mesh = new Mesh();
    mesh.units = top.units;

    // Add all vertices: top then bottom
    mesh.vertices.AddRange(top.vertices);
    mesh.vertices.AddRange(bottom.vertices);

    // === Add TOP faces (unchanged) ===
    int i = 0;
    while (i < top.faces.Count)
    {
      int faceType = top.faces[i];
      if ((faceType == 0 || faceType == 3) && i + 3 < top.faces.Count)
      {
        mesh.faces.AddRange(top.faces.GetRange(i, 4));
        i += 4;
      }
      else if (faceType == 4 && i + 5 < top.faces.Count)
      {
        mesh.faces.AddRange(top.faces.GetRange(i, 5));
        i += 5;
      }
      else
      {
        SpeckleLog.Logger.Warning($"[ConnectTopAndBottom] ⚠ Unknown face type {faceType}, skipping.");
        i += faceType + 1;
      }
    }

    // === Add BOTTOM faces (indices need offset) ===
    i = 0;
    while (i < bottom.faces.Count)
    {
      int faceType = bottom.faces[i];
      if ((faceType == 0 || faceType == 3) && i + 3 < bottom.faces.Count)
      {
        mesh.faces.Add(faceType); // Preserve 0 or 3
        mesh.faces.Add(bottom.faces[i + 1] + numVertices);
        mesh.faces.Add(bottom.faces[i + 2] + numVertices);
        mesh.faces.Add(bottom.faces[i + 3] + numVertices);
        i += 4;
      }
      else if (faceType == 4 && i + 5 < bottom.faces.Count)
      {
        mesh.faces.Add(4);
        mesh.faces.Add(bottom.faces[i + 1] + numVertices);
        mesh.faces.Add(bottom.faces[i + 2] + numVertices);
        mesh.faces.Add(bottom.faces[i + 3] + numVertices);
        mesh.faces.Add(bottom.faces[i + 4] + numVertices);
        i += 5;
      }
      else
      {
        SpeckleLog.Logger.Warning($"[ConnectTopAndBottom] ⚠ Unknown face type {faceType}, skipping.");
        i += faceType + 1;
      }
    }

    // === Add SIDE faces ===
    for (int v = 0; v < numVertices; v++)
    {
      int next = (v + 1) % numVertices;
      mesh.faces.Add(4); // quad
      mesh.faces.Add(v);
      mesh.faces.Add(next);
      mesh.faces.Add(next + numVertices);
      mesh.faces.Add(v + numVertices);
    }

    mesh["debug_tag"] = "side_connected";
    SpeckleLog.Logger.Information($"[ConnectTopAndBottom] ✅ Final face count: {mesh.faces.Count}");
    return mesh;
  }

  List<int[]> TriangulateWithHoleSupport(List<List<Point>> loops, int offset)
  {
    if (loops.Count != 2)
      throw new Exception("Currently only supports one hole");

    var outer = loops[0];
    var hole = loops[1];

    // Very simple rectangular subtraction — only works for rectangles.
    // Returns: top surface triangles only.

    List<int[]> tris = new();

    // Get rectangle corner indices
    int a = offset + 0;
    int b = offset + 1;
    int c = offset + 2;
    int d = offset + 3;

    int ha = offset + 4;
    int hb = offset + 5;
    int hc = offset + 6;
    int hd = offset + 7;

    // Manually triangulate slab with hole
    tris.Add(new[] { a, b, hb });
    tris.Add(new[] { a, hb, ha });

    tris.Add(new[] { b, c, hc });
    tris.Add(new[] { b, hc, hb });

    tris.Add(new[] { c, d, hd });
    tris.Add(new[] { c, hd, hc });

    tris.Add(new[] { d, a, ha });
    tris.Add(new[] { d, ha, hd });

    return tris;
  }

  private bool IsPointInsideArea(Node pt, List<Node> areaNodes)
  {
    var polygon = new NetTopologySuite.Geometries.Polygon(
      new NetTopologySuite.Geometries.LinearRing(
        areaNodes.Select(p => new NetTopologySuite.Geometries.Coordinate(p.basePoint.x, p.basePoint.y)).ToArray()
      )
    );

    var point = new NetTopologySuite.Geometries.Point(pt.basePoint.x, pt.basePoint.y);
    return polygon.Contains(point);
  }

  private string FindSlabForOpening(Element2D opening, List<Element2D> candidateSlabs)
  {
    foreach (var slab in candidateSlabs)
    {
      if (opening.topology.All(pt => IsPointInsideArea(pt, slab.topology)))
        return slab.name;
    }
    return null;
  }


  private bool InjectOpeningIntoSlab(Element2D opening, out string openingName)
  {
    openingName = $"Opening_{Guid.NewGuid().ToString().Substring(0, 8)}";

    if (opening.topology == null || opening.topology.Count < 3)
      throw new ConversionException("❌ Opening has invalid topology (less than 3 points).");

    var x = new double[opening.topology.Count];
    var y = new double[opening.topology.Count];
    var z = new double[opening.topology.Count];

    for (int i = 0; i < opening.topology.Count; i++)
    {
      var pt = opening.topology[i].basePoint;
      x[i] = ScaleToNative(pt.x, pt.units);
      y[i] = ScaleToNative(pt.y, pt.units);
      z[i] = ScaleToNative(pt.z, pt.units);
    }

    int n = 0;
    string[] props = Array.Empty<string>();
    Model.PropArea.GetNameList(ref n, ref props);
    string propName = props.FirstOrDefault() ?? "Default";

    int result = Model.AreaObj.AddByCoord(opening.topology.Count, ref x, ref y, ref z, ref openingName, propName);
    if (result != 0) return false;

    result = Model.AreaObj.SetOpening(openingName, true);
    if (result != 0) return false;

    if (!string.IsNullOrEmpty(opening.applicationId))
      Model.AreaObj.SetGUID(openingName, opening.applicationId);

    return true;
  }

  private List<Line> CreateOpeningCrossLines(List<Node> nodes)
  {
    var points = nodes.Select(n => n.basePoint).ToList();
    if (points.Count < 4)
      return new List<Line>(); // Not enough points for cross

    // Find corner pairs (assumes rectangular openings in order)
    var p1 = points[0];
    var p2 = points[2]; // opposite corner
    var p3 = points[1];
    var p4 = points[3];

    var line1 = new Line(p1, p2) { units = p1.units };
    var line2 = new Line(p3, p4) { units = p1.units };

    // Optional: set color black
    int black = unchecked((int)0xFF000000);
    line1["color"] = black;
    line2["color"] = black;

    return new List<Line> { line1, line2 };
  }




  //Log Methods Below

  private void LogLoopDiagnostics(List<List<Node>> loops)
  {
    for (int i = 0; i < loops.Count; i++)
    {
      var loop = loops[i];
      string units = loop.FirstOrDefault()?.basePoint.units ?? "m";

      SpeckleLog.Logger.Information($"[Loop {i}] Points: {loop.Count}");

      // 🔍 Log each point's unique name and coordinates
      for (int j = 0; j < loop.Count; j++)
      {
        var node = loop[j];
        var pt = node.basePoint;
        string pointName = node.id ?? "Unnamed";
        SpeckleLog.Logger.Information(
          $"[Loop {i}] Point {j}: Name = {pointName}, X = {pt.x:F2}, Y = {pt.y:F2}, Z = {pt.z:F2}"
        );
      }

      // Check if closed
      var first = loop.First().basePoint;
      var last = loop.Last().basePoint;
      bool closed = Math.Abs(first.x - last.x) < 1e-6 &&
                    Math.Abs(first.y - last.y) < 1e-6 &&
                    Math.Abs(first.z - last.z) < 1e-6;
      SpeckleLog.Logger.Information($"[Loop {i}] Is closed: {closed}");

      // Bounding box
      var xs = loop.Select(p => p.basePoint.x);
      var ys = loop.Select(p => p.basePoint.y);
      var zs = loop.Select(p => p.basePoint.z);

      double minX = xs.Min(), maxX = xs.Max();
      double minY = ys.Min(), maxY = ys.Max();
      double minZ = zs.Min(), maxZ = zs.Max();

      SpeckleLog.Logger.Information($"[Loop {i}] BBox X: [{minX:F2}, {maxX:F2}]");
      SpeckleLog.Logger.Information($"[Loop {i}] BBox Y: [{minY:F2}, {maxY:F2}]");
      SpeckleLog.Logger.Information($"[Loop {i}] BBox Z: [{minZ:F2}, {maxZ:F2}]");

      // Signed area to guess winding direction
      double signedArea = 0;
      for (int j = 0; j < loop.Count; j++)
      {
        var p1 = loop[j].basePoint;
        var p2 = loop[(j + 1) % loop.Count].basePoint;
        signedArea += (p1.x * p2.y - p2.x * p1.y);
      }
      signedArea *= 0.5;

      SpeckleLog.Logger.Information($"[Loop {i}] Signed 2D area: {signedArea:F6} ({(signedArea > 0 ? "CCW" : "CW")})");
      SpeckleLog.Logger.Information($"[Loop {i}] ---");
    }
  }

  public void LogPolygonValidity(List<Node> loop, int loopIndex)
  {
    var factory = new NTS.GeometryFactory();
    var coords = loop.Select(n => new NTS.Coordinate(n.basePoint.x, n.basePoint.y)).ToList();

    if (!coords.First().Equals2D(coords.Last()))
      coords.Add(coords.First());

    var linearRing = factory.CreateLinearRing(coords.ToArray());
    var polygon = factory.CreatePolygon(linearRing);

    if (!polygon.IsValid)
    {
      var reason = new IsValidOp(polygon).ValidationError;
      SpeckleLog.Logger.Warning($"[ValidityCheck] ❌ Loop {loopIndex} is invalid: {reason?.Message}");
      SpeckleLog.Logger.Warning($"[ValidityCheck] 💬 Error location: {reason?.Coordinate}");
    }
    else
    {
      SpeckleLog.Logger.Information($"[ValidityCheck] ✅ Loop {loopIndex} is valid.");
    }
  }
}
#endregion
