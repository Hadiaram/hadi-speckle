using System;
using System.Collections.Generic;
using System.Linq;
using ConverterCSIShared.Models;
#if ETABS22
using ETABSv1;
#else
using CSiAPIv1;
#endif
using Objects.BuiltElements;
using Objects.Structural.Analysis;
using Objects.Structural.CSI.Analysis;
using Objects.Structural.CSI.Properties;
using Objects.Structural.Geometry;
using Objects.Structural.Loading;
using Objects.Structural.Materials;
using Objects.Structural.Properties;
using Objects.Structural.Results;
using Serilog;
using Speckle.Core.Api.GraphQL;
using Speckle.Core.Kits;
using Speckle.Core.Kits.ConverterInterfaces;
using Speckle.Core.Logging;
using Speckle.Core.Models;
using Speckle.Newtonsoft.Json.Linq;
using OSG = Objects.Structural.Geometry;
using Objects.Converter.CSI;

namespace Objects.Converter.CSI;

public partial class ConverterCSI : ISpeckleConverter, IFinalizable
{
#if ETABS
  public static string CSIAppName = HostApplications.ETABS.Name;
  public static string CSISlug = HostApplications.ETABS.Slug;
#elif SAP2000
  public static string CSIAppName = HostApplications.SAP2000.Name;
  public static string CSISlug = HostApplications.SAP2000.Slug;
#elif CSIBRIDGE
  public static string CSIAppName = HostApplications.CSiBridge.Name;
  public static string CSISlug = HostApplications.CSiBridge.Slug;
#elif SAFE
  public static string CSIAppName = HostApplications.SAFE.Name;
  public static string CSISlug = HostApplications.SAFE.Slug;
#endif
  public string Description => "Default Speckle Kit for CSI";

  public string Name => nameof(ConverterCSI);

  public string Author => "Speckle";

  public string WebsiteOrEmail => "https://speckle.systems";

  public cSapModel Model { get; private set; }
  public string ProgramVersion { get; private set; }

  public Model SpeckleModel { get; set; }

  public ReceiveMode ReceiveMode { get; set; }

  /// <summary>
  /// <para>To know which objects are already in the model. These are *mostly* elements that are in the model before the receive operation starts, but certain names will be added for objects that may be referenced by other elements such as load patterns and load cases.</para>
  /// <para> The keys are typically GUIDS and the values are exclusively names. It is easier to retrieve names, and names are typically used by the api, however GUIDS are more stable and can't be changed in the user interface. Some items (again load patterns and load combinations) don't have GUIDs so those just store the name value twice. </para>
  /// </summary>
  public Dictionary<string, string> ExistingObjectGuids { get; set; }

  /// <summary>
  /// <para>To know which other objects are being converted, in order to sort relationships between them.
  /// For example, elements that have children use this to determine whether they should send their children out or not.</para>
  /// </summary>
  public List<ApplicationObject> ContextObjects { get; set; } = new List<ApplicationObject>();

  /// <summary>
  /// <para>To keep track of previously received objects from a given stream in here. If possible, conversions routines
  /// will edit an existing object, otherwise they will delete the old one and create the new one.</para>
  /// </summary>
  public List<ApplicationObject> PreviousContextObjects { get; set; } = new List<ApplicationObject>();
  public Dictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>();

  public void SetContextObjects(List<ApplicationObject> objects) => ContextObjects = objects;

  public void SetPreviousContextObjects(List<ApplicationObject> objects) => PreviousContextObjects = objects;

  private ResultsConverter? resultsConverter;

  public void SetContextDocument(object doc)
  {
    Model = (cSapModel)doc;
    double version = 0;
    string versionString = null;
    Model.GetVersion(ref versionString, ref version);
    ProgramVersion = versionString;

    if (!Settings.ContainsKey("operation"))
    {
      throw new Exception("operation setting was not set before calling converter.SetContextDocument");
    }

    if (Settings["operation"] == "receive")
    {
      ExistingObjectGuids = GetAllGuids(Model);
      // TODO: make sure we are setting the load patterns before we import load combinations
    }
    else if (Settings["operation"] == "send")
    {
      LoadTablesOnce();

      SpeckleModel = ModelToSpeckle();
      resultsConverter = new ResultsConverter(Model, Settings, GetLoadCases(), GetLoadCombos());
    }
    else
    {
      throw new Exception("operation setting was not set to \"send\" or \"receive\"");
    }
  }

  public void SetConverterSettings(object settings)
  {
    Settings = settings as Dictionary<string, string>;
  }

  public HashSet<Exception> ConversionErrors { get; private set; } = new HashSet<Exception>();
  public ProgressReport Report { get; private set; } = new ProgressReport();

  public bool CanConvertToNative(Base @object)
  {
    Log.Information($"👁 Checking object type: {@object.speckle_type}");
    switch (@object)
    {
      case Element2D elem:
        SpeckleLog.Logger
  .ForContext<ConverterCSI>()
  .Information($"🧱 Accepting Element2D: {elem.name}");
        return true;

      case CSIDiaphragm _:
      case CSIStories _:
      case Element1D _:
      case Load _:
      case Geometry.Line _:  // Enabled for ETABS22
      case Node _:
      case GridLine _:
      //case Model o:
      //case Property property:

      // for the moment we need to have this in here so the flatten traversal skips over this object
      // otherwise it would add result.element to the list twice and the stored objects dictionary would throw
      case Result _:
      case BuiltElements.Beam _:
      case BuiltElements.Brace _:
      case BuiltElements.Column _:
      case StructuralMaterial _:
        return true;
    }
    ;
    return false;
  }

  public bool CanConvertToNativeDisplayable(Base @object)
  {
    return false;
  }

  public bool CanConvertToSpeckle(object @object)
  {
    if (@object == null)
    {
      return false;
    }

    foreach (var type in Enum.GetNames(typeof(ConverterCSI.CSIAPIUsableTypes)))
    {
      if (type == @object.ToString())
      {
        return true;
      }
    }
    return false;
  }

  public object ConvertToNative(Base @object)
  {
    ApplicationObject appObj = new(@object.id, @object.speckle_type) { applicationId = @object.applicationId };

    List<string> convertedNames = new();
    string? convertedName = null;

    switch (@object)
    {
      case CSIAreaSpring o:
        convertedName = AreaSpringPropertyToNative(o);
        break;
      case CSIDiaphragm o:
        convertedName = DiaphragmToNative(o);
        break;
      case CSILinearSpring o:
        convertedName = LinearSpringPropertyToNative(o);
        break;
      case CSILinkProperty o:
        convertedName = LinkPropertyToNative(o);
        break;
      case CSIProperty2D o:
        convertedName = Property2DToNative(o);
        break;
      case CSISpringProperty o:
        convertedName = SpringPropertyToNative(o);
        break;
      case CSIStories o:
        convertedNames = StoriesToNative(o);
        break;
      // case CSIWindLoadingFace o:
      //   convertedName = LoadFaceToNative(o, appObj.Log);
      //   break;
      // case CSITendonProperty o:
      case OSG.Element1D o:
        FrameToNative(o, appObj);
        break;
      case OSG.Element2D o:
        AreaToNative(o, appObj);
        break;
      case LoadBeam o:
        convertedNames = LoadFrameToNative(o, appObj.Log);
        break;
      case LoadFace o:
        convertedName = LoadFaceToNative(o, appObj.Log);
        break;
      case Geometry.Line o:
        convertedName = LineToNative(o); // do we really want to assume any line is a frame object?
        break;
      case OSG.Node o:
        convertedName = PointToNative(o, appObj.Log);
        break;
      case Property1D o:
        convertedName = Property1DToNative(o);
        break;
      case StructuralMaterial o:
        convertedName = MaterialToNative(o);
        break;
      case BuiltElements.Beam o:
        CurveBasedElementToNative(o, o.baseLine, appObj);
        break;
      case BuiltElements.Brace o:
        CurveBasedElementToNative(o, o.baseLine, appObj);
        break;
      case BuiltElements.Column o:
        CurveBasedElementToNative(o, o.baseLine, appObj);
        break;
      case GridLine o:
        GridLineToNative(o);
        break;
      default:
        throw new ConversionNotSupportedException($"{@object.GetType()} is an unsupported type");
    }

    if (convertedName is not null)
    {
      convertedNames.Add(convertedName);
    }

    appObj.Update(createdIds: convertedNames);

    return appObj;
  }

  public object ConvertToNativeDisplayable(Base @object)
  {
    throw new NotImplementedException();
  }

  public List<object> ConvertToNative(List<Base> objects)
  {
    return objects.Select(x => ConvertToNative(x)).ToList();
  }

  public Base ConvertToSpeckle(object @object)
  {
    (string type, string name) = ((string, string))@object;
    Base returnObject = null;
    switch (type)
    {
      case "Point":
        returnObject = PointToSpeckle(name);
        Log.Information($"Created Node");
        break;
      case "Frame":
        SpeckleLog.Logger.ForContext<ConverterCSI>().Information($"[ConvertToSpeckle] Dispatching frame '{name}' to FrameToSpeckle");
        returnObject = FrameToSpeckle(name);
        SpeckleLog.Logger.ForContext<ConverterCSI>().Information($"Created Frame");
        break;
      case "Model":
        returnObject = SpeckleModel;
        break;
      case "AnalysisResults":
        returnObject = ResultsToSpeckle();
        break;
      case "Stories":
        returnObject = StoriesToSpeckle();
        break;
      case "Area":
        returnObject = AreaToSpeckle(name);
        Log.Information($"Created Area");
        break;
      case "Wall":
        returnObject = WallToSpeckle(name);
        Log.Information($"Created Wall");
        break;
      case "Floor":
        returnObject = FloorToSpeckle(name);
        Log.Information($"Created Floor");
        break;
      case "Column":
        returnObject = ColumnToSpeckle(name);
        Log.Information($"Created Column");
        break;
      case "Beam":
        returnObject = BeamToSpeckle(name);
        Log.Information($"Created Beam");
        break;
      case "Brace":
        returnObject = BraceToSpeckle(name);
        Log.Information($"Created Brace");
        break;
      case "Link":
        returnObject = LinkToSpeckle(name);
        Log.Information ($"Created Link");
        break;
      case "ElementsCount":
        returnObject = ModelElementsCountToSpeckle();
        break;
      case "Spandrel":
        returnObject = SpandrelToSpeckle(name);
        Log.Information($"Created Spandrel");
        break;
      case "Pier":
        returnObject = PierToSpeckle(name);
        Log.Information($"Created Pier");
        break;
      case "Grids":
        returnObject = gridLinesToSpeckle(name);
        Log.Information($"Created Grids");
        break;
      case "Tendon":
        returnObject = CSITendonToSpeckle(name);
        Log.Information($"Created Tendons");
        break;
      //case "Diaphragm":
      //  returnObject = diaphragmToSpeckle(name);
      //  Report.Log($"Created Diaphragm");
      case "Links":
        returnObject = LinkToSpeckle(name);
        break;
      //case "LoadCase":
      //    returnObject = LoadCaseToSpeckle(name);
      //    break;
      case "BeamLoading":
        returnObject = LoadFrameToSpeckle(name, GetBeamNames(Model).Count());
        Log.Information($"Created Loading Beam");
        break;
      case "ColumnLoading":
        returnObject = LoadFrameToSpeckle(name, GetColumnNames(Model).Count());
        Log.Information($"Created Loading Column");
        break;
      case "BraceLoading":
        returnObject = LoadFrameToSpeckle(name, GetBraceNames(Model).Count());
        Log.Information($"Created Loading Brace");
        break;
      case "FrameLoading":
        returnObject = LoadFrameToSpeckle(name, GetAllFrameNames(Model).Count());
        Log.Information($"Created Loading Frame");
        break;
      case "FloorLoading":
        returnObject = LoadFaceToSpeckle(name, GetAllFloorNames(Model).Count());
        Log.Information($"Created Loading Floor");
        break;
      case "WallLoading":
        returnObject = LoadFaceToSpeckle(name, GetAllWallNames(Model).Count());
        Log.Information($"Created Loading Wall");
        break;  
      case "AreaLoading":
        returnObject = LoadFaceToSpeckle(name, GetAllAreaNames(Model).Count());
        Log.Information($"Created Loading Area");
        break;
      case "NodeLoading":
        returnObject = LoadNodeToSpeckle(name, GetAllPointNames(Model).Count());
        Log.Information($"Created Loading Node");
        break;
      case "LoadPattern":
        returnObject = LoadPatternToSpeckle(name);
        Log.Information($"Created Loading Pattern");
        break;
      //case "ColumnResults":
      //    returnObject = FrameResultSet1dToSpeckle(name);
      //    break;
      //case "BeamResults":
      //    returnObject = FrameResultSet1dToSpeckle(name);
      //    break;
      //case "BraceResults":
      //    returnObject = FrameResultSet1dToSpeckle(name);
      //    break;
      //case "PierResults":
      //    returnObject = PierResultSet1dToSpeckle(name);
      //    break;
      //case "SpandrelResults":
      //    returnObject = SpandrelResultSet1dToSpeckle(name);
      //    break;
      //case "GridSys":
      //    returnObject = GridSysToSpeckle(name);
      //    break;
      //case "Combo":
      //    returnObject = ComboToSpeckle(name);
      //    break;
      //case "DesignSteel":
      //    returnObject = DesignSteelToSpeckle(name);
      //    break;
      //case "DeisgnConcrete":
      //    returnObject = DesignConcreteToSpeckle(name);
      //    break;
      //case "Story":
      //    returnObject = StoryToSpeckle(name);
      //    break;
      //case "Diaphragm":
      //    returnObject = DiaphragmToSpeckle(name);
      //    break;
      //case "PierLabel":
      //    returnObject = PierLabelToSpeckle(name);
      //    break;
      //case "PropAreaSpring":
      //    returnObject = PropAreaSpringToSpeckle(name);
      //    break;
      //case "PropLineSpring":
      //    returnObject = PropLineSpringToSpeckle(name);
      //    break;
      //case "PropPointSpring":
      //    returnObject = PropPointSpringToSpeckle(name);
      //    break;
      //case "SpandrelLabel":
      //    returnObject = SpandrelLabelToSpeckle(name);
      //    break;
      //case "PropTendon":
      //    returnObject = PropTendonToSpeckle(name);
      //    break;
      //case "PropLink":
      //    returnObject = PropLinkToSpeckle(name);
      //    break;
      //default:
      //    ConversionErrors.Add(new SpeckleException($"Skipping not supported type: {type}"));
      //    returnObject = null;
      //    break;
    }

    // send the object out with the same appId that it came in with for updating purposes
    if (returnObject != null)
    {
      returnObject.applicationId = GetOriginalApplicationId(returnObject.applicationId);
    }

    return returnObject;
  }

  public List<Base> ConvertToSpeckle(List<object> objects)
  {
    return objects.Select(x => ConvertToSpeckle(x)).ToList();
  }

  public IEnumerable<string> GetServicedApplications()
  {
#if ETABS22
    return new[] { "ETABS", "ETABS22" };
#else
    return new[] { CSIAppName };
#endif
  }

  public void FinalizeConversion()
  {
    CommitAllDatabaseTableChanges();
  }

  #region Load Set
  public class ResolvedLoadRow
  {
    public string Name { get; set; } // Load set name
    public string LoadPattern { get; set; }
    public double LoadValue { get; set; }
    public string GUID { get; set; }
    public string LoadSet { get; set; } // ✅ Add this field

    public ResolvedLoadRow(string name, string loadPattern, double loadValue, string guid, string loadSet)
    {
      Name = name;
      LoadPattern = loadPattern;
      LoadValue = loadValue;
      GUID = guid;
      LoadSet = loadSet; // ✅ Assign it here
    }
  }

  private Dictionary<string, string> areaToLoadSet = new();
  private List<ResolvedLoadRow> allResolvedRows = new();

  public void LoadTablesOnce()
  {
    LoadAreaAssignments();
    LoadResolvedShellLoads();
  }

  private void LoadAreaAssignments()
  {
    string tableName = "Area Load Assignments - Uniform Load Sets";
    string[] fieldKeys = new string[0], fieldsIncluded = new string[0], data = new string[0];
    int version = 0, numRecords = 0;
    string group = "All";

    int res = Model.DatabaseTables.GetTableForDisplayArray(tableName, ref fieldKeys, group, ref version, ref fieldsIncluded, ref numRecords, ref data);
    if (res != 0) return;

    int fArea = Array.IndexOf(fieldsIncluded, "UniqueName");
    int fLoadSet = Array.IndexOf(fieldsIncluded, "LoadSet");

    if (fArea == -1 || fLoadSet == -1)
    {
      SpeckleLog.Logger.Warning($"[LoadAreaAssignments] ❌ Could not find required columns in '{tableName}'. Found fields: {string.Join(", ", fieldsIncluded)}");
      return;
    }

    for (int i = 0; i < numRecords; i++)
    {
      string area = data[i * fieldsIncluded.Length + fArea];
      string loadSet = data[i * fieldsIncluded.Length + fLoadSet];

      areaToLoadSet[area] = loadSet;
    }

    SpeckleLog.Logger.Information($"[LoadAreaAssignments] ✅ Loaded {areaToLoadSet.Count} area→load set assignments.");
  }


  private void LoadResolvedShellLoads()
  {

    string tableName = "Shell Uniform Load Sets";
    string[] fieldKeys = new string[0], fieldsIncluded = new string[0], data = new string[0];
    int version = 0, numRecords = 0;
    string group = "All";

    int res = Model.DatabaseTables.GetTableForDisplayArray(tableName, ref fieldKeys, group, ref version, ref fieldsIncluded, ref numRecords, ref data);
    if (res != 0)
    {
      SpeckleLog.Logger.Warning($"[LoadResolvedShellLoads] ❌ Failed to retrieve table '{tableName}'");
      return;
    }

    SpeckleLog.Logger.Information($"[LoadResolvedShellLoads] Found fields: {string.Join(", ", fieldsIncluded)}");

    int fName = Array.IndexOf(fieldsIncluded, "Name");
    int fPattern = Array.IndexOf(fieldsIncluded, "LoadPattern");
    int fValue = Array.IndexOf(fieldsIncluded, "LoadValue");
    int fGuid = Array.IndexOf(fieldsIncluded, "GUID");

    if (fName == -1 || fPattern == -1 || fValue == -1 || fGuid == -1)
    {
      SpeckleLog.Logger.Warning($"[LoadResolvedShellLoads] ❌ Missing required fields in '{tableName}'");
      return;
    }

    int fieldCount = fieldsIncluded.Length;
    allResolvedRows.Clear(); // If needed

    for (int i = 0; i < numRecords; i++)
    {
      string name = data[i * fieldCount + fName];
      string pattern = data[i * fieldCount + fPattern];
      string valueStr = data[i * fieldCount + fValue];
      string guid = data[i * fieldCount + fGuid];

      if (double.TryParse(valueStr, out double loadValue))
      {
        string loadSet = name; // Using 'name' as load set
        allResolvedRows.Add(new ResolvedLoadRow(name, pattern, loadValue, guid, loadSet));
      }
      else
      {
        SpeckleLog.Logger.Warning($"[LoadResolvedShellLoads] ⚠️ Could not parse load value '{valueStr}' for '{name}'");
      }
    }

    SpeckleLog.Logger.Information($"[LoadResolvedShellLoads] ✅ Loaded {allResolvedRows.Count} resolved load entries.");
  }

  public void LogResolvedLoadSetForArea(string areaName)
  {
    var logger = SpeckleLog.Logger.ForContext<ConverterCSI>();

    if (!areaToLoadSet.TryGetValue(areaName, out string loadSet))
    {
      logger.Warning($"❌ No load set assigned to area '{areaName}'.");
      return;
    }

    logger.Information($"🔍 Area '{areaName}' is assigned load set: {loadSet}");

    var matches = allResolvedRows
      .Where(row => row.Name == loadSet) // This is correct: match load set name
      .ToList();

    if (!matches.Any())
    {
      logger.Warning($"⚠️ No resolved shell loads found for load set '{loadSet}'.");
      return;
    }

    foreach (var row in matches)
    {
      logger.Information($"[LoadSet: {row.Name}] Pattern: {row.LoadPattern}, Value: {row.LoadValue}, GUID: {row.GUID}");
    }
  }

  public class LoadSetData : Base
  {
    public string LoadSet { get; set; }
    public List<SerializableResolvedLoadRow> ResolvedLoads { get; set; }

    public LoadSetData() { }

    public LoadSetData(string loadSet, List<SerializableResolvedLoadRow> resolvedLoads)
    {
      LoadSet = loadSet;
      ResolvedLoads = resolvedLoads;
    }
  }


  public class SerializableResolvedLoadRow : Base
  {
    public string UniqueName { get; set; }
    public string LoadPattern { get; set; }
    public double Value { get; set; }
    public string Direction { get; set; }
    public string CoordSys { get; set; }
  }
  #endregion

  private bool sendExtrudedGeometry = true; // default true for now

  public void SetSendAsExtruded(bool sendExtruded)
  {
    this.sendExtrudedGeometry = sendExtruded;
  }

}
