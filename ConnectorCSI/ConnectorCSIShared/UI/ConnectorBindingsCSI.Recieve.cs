using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ConnectorCSI.Storage;
using DesktopUI2;
using DesktopUI2.Models;
using DesktopUI2.ViewModels;
using Serilog.Context;
using Speckle.ConnectorCSI.Util;
using Speckle.Core.Api;
using Speckle.Core.Kits;
using Speckle.Core.Kits.ConverterInterfaces;
using Speckle.Core.Logging;
using Speckle.Core.Models;
using Speckle.Core.Models.GraphTraversal;
using Objects.Structural.Geometry;

namespace Speckle.ConnectorCSI.UI;

public partial class ConnectorBindingsCSI : ConnectorBindings
{
  public List<ApplicationObject> Preview { get; set; } = new List<ApplicationObject>();
  public Dictionary<string, Base> StoredObjects = new();
  public override bool CanPreviewReceive => false;

  public override Task<StreamState> PreviewReceive(StreamState state, ProgressViewModel progress)
  {
    return null;
  }

  public override async Task<StreamState> ReceiveStream(StreamState state, ProgressViewModel progress)
  {
    Exceptions.Clear();

#if ETABS22
    SpeckleLog.Logger.Information("✅ Using direct converter reference for ETABS22 receive");

    // CRITICAL: Initialize KitManager to register types for deserialization
    // Even though we're not using KitManager to load the converter,
    // the deserializer needs KitManager.Types to be populated
    // Otherwise all objects deserialize as plain Base!

    // Force load the Objects assembly BEFORE initializing KitManager
    var objectsAssembly = typeof(Objects.Structural.Geometry.Element1D).Assembly;
    SpeckleLog.Logger.Information("🔍 Objects assembly loaded: {Assembly}", objectsAssembly.FullName);
    SpeckleLog.Logger.Information("🔍 Objects assembly location: {Location}", objectsAssembly.Location);

    // Manually instantiate ObjectsKit to ensure its types are available
    var objectsKit = new Objects.ObjectsKit();
    var objectsKitTypes = objectsKit.Types.ToList();
    SpeckleLog.Logger.Information("📦 ObjectsKit has {Count} types", objectsKitTypes.Count);
    SpeckleLog.Logger.Information("   Sample types: {Types}",
      string.Join(", ", objectsKitTypes.Take(5).Select(t => t.Name)));

    // Now initialize KitManager - it should pick up our loaded Objects assembly
    var kits = KitManager.Kits; // This triggers KitManager.Initialize()
    SpeckleLog.Logger.Information("🔧 KitManager initialized - {Count} kits loaded", kits.Count());

    foreach (var kit in kits)
    {
      SpeckleLog.Logger.Information("   Kit: {Name} ({TypeCount} types)", kit.Name, kit.Types.Count());
    }

    // Verify types are registered in KitManager
    var typesCount = KitManager.Types.Count();
    SpeckleLog.Logger.Information("🔍 KitManager has {Count} types registered", typesCount);

    if (typesCount < 100)
    {
      SpeckleLog.Logger.Warning("⚠️ KitManager has very few types! Manually injecting ObjectsKit types...");

      // Use reflection to inject types into KitManager's internal list
      var kitManagerType = typeof(KitManager);
      var availableTypesField = kitManagerType.GetField("s_availableTypes",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

      if (availableTypesField != null)
      {
        var currentTypes = (List<Type>)availableTypesField.GetValue(null);
        // Add ObjectsKit types if not already present
        foreach (var type in objectsKitTypes)
        {
          if (!currentTypes.Contains(type))
          {
            currentTypes.Add(type);
          }
        }
        SpeckleLog.Logger.Information("✅ Manually added {Count} types to KitManager", objectsKitTypes.Count);
        SpeckleLog.Logger.Information("🔍 KitManager now has {Count} types", KitManager.Types.Count());
      }
    }

    // Log first 10 types to verify
    var registeredTypes = KitManager.Types.Take(10).ToList();
    SpeckleLog.Logger.Information("📋 First 10 registered types:");
    foreach (var type in registeredTypes)
    {
      SpeckleLog.Logger.Information("   - {Type}", type.FullName);
    }

    // Check if Element1D is registered (key type for structural elements)
    var hasElement1D = KitManager.Types.Any(t => t.Name == "Element1D");
    SpeckleLog.Logger.Information("🔍 Element1D type registered: {Registered}", hasElement1D);

    // Direct instantiation - no assembly loading, preserves type identity
    var converter = new Objects.Converter.CSI.ConverterCSI();
    SpeckleLog.Logger.Information("✅ Created ConverterCSI instance for receive");
    SpeckleLog.Logger.Information("🔍 Converter type: {Type}", converter.GetType().FullName);
#else
    SpeckleLog.Logger.Information("✅ Using default kit manager for receive");
    var kit = KitManager.GetDefaultKit();
    var appName = GetHostAppVersion(Model);
    ISpeckleConverter converter = kit.LoadConverter(appName);
    SpeckleLog.Logger.Information("✅ Loaded converter: {Type}", converter.GetType().FullName);
#endif

    // set converter settings as tuples (setting slug, setting selection)
    // for csi, these must go before the SetContextDocument method.
    var settings = new Dictionary<string, string>();
    foreach (var setting in state.Settings)
    {
      settings.Add(setting.Slug, setting.Selection);
    }

    settings.Add("operation", "receive");
    converter.SetConverterSettings(settings);

    converter.SetContextDocument(Model);

    SpeckleLog.Logger.Information("🚀 DIAGNOSTIC CODE ACTIVE - Enhanced logging enabled (commit f601254)");

    Exceptions.Clear();
    var previouslyReceivedObjects = state.ReceivedObjects;

    progress.CancellationToken.ThrowIfCancellationRequested();

    Exceptions.Clear();

    Commit commit = await ConnectorHelpers.GetCommitFromState(state, progress.CancellationToken);
    state.LastCommit = commit;
    Base commitObject = await ConnectorHelpers.ReceiveCommit(commit, state, progress);
    await ConnectorHelpers.TryCommitReceived(state, commit, GetHostAppVersion(Model), progress.CancellationToken);

    Preview.Clear();
    StoredObjects.Clear();

    //Execute.PostToUIThread(() => state.Progress.Maximum = state.SelectedObjectIds.Count());

    Preview = FlattenCommitObject(commitObject, converter, msg => {
      progress.Report.Log(msg);
      SpeckleLog.Logger.Information(msg);
    });
    SpeckleLog.Logger.Information("🔍 Objects returned by traversal: {Count}", Preview.Count);
    progress.Report.Log($"🔍 Objects returned by traversal: {Preview.Count}");
    foreach (var previewObj in Preview)
    {
      progress.Report.Log(previewObj);
    }

    converter.ReceiveMode = state.ReceiveMode;
    // needs to be set for editing to work
    converter.SetPreviousContextObjects(previouslyReceivedObjects);

    progress.CancellationToken.ThrowIfCancellationRequested();

    StreamStateManager.SaveBackupFile(Model);

    using var d0 = LogContext.PushProperty("converterName", converter.Name);
    using var d1 = LogContext.PushProperty("converterAuthor", converter.Author);
    using var d2 = LogContext.PushProperty("conversionDirection", nameof(ISpeckleConverter.ConvertToNative));
    using var d3 = LogContext.PushProperty("converterSettings", settings);
    using var d4 = LogContext.PushProperty("converterReceiveMode", converter.ReceiveMode);

    var newPlaceholderObjects = ConvertReceivedObjects(converter, progress);

    SpeckleLog.Logger.Information("📊 Conversion Summary:");
    SpeckleLog.Logger.Information("   Total objects processed: {Count}", newPlaceholderObjects.Count);
    SpeckleLog.Logger.Information("   Created: {Count}", newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Created));
    SpeckleLog.Logger.Information("   Updated: {Count}", newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Updated));
    SpeckleLog.Logger.Information("   Failed: {Count}", newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Failed));
    SpeckleLog.Logger.Information("   Skipped: {Count}", newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Skipped));

    progress.Report.Log($"📊 Conversion Summary:");
    progress.Report.Log($"   Total objects processed: {newPlaceholderObjects.Count}");
    progress.Report.Log($"   Created: {newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Created)}");
    progress.Report.Log($"   Updated: {newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Updated)}");
    progress.Report.Log($"   Failed: {newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Failed)}");
    progress.Report.Log($"   Skipped: {newPlaceholderObjects.Count(o => o.Status == ApplicationObject.State.Skipped)}");

    DeleteObjects(previouslyReceivedObjects, newPlaceholderObjects, progress);

    // The following block of code is a hack to properly refresh the view
    // This bug exists in both ETABS and ETABS22
#if ETABS || ETABS22
    if (newPlaceholderObjects.Any(o => o.Status == ApplicationObject.State.Updated))
    {
      SpeckleLog.Logger.Information("🔄 Refreshing database table for updated objects");
      progress.Report.Log($"🔄 Refreshing database table for updated objects");
      RefreshDatabaseTable("Beam Object Connectivity");
    }
#endif

    SpeckleLog.Logger.Information("🔄 Refreshing ETABS view (RefreshWindow + RefreshView)");
    progress.Report.Log($"🔄 Refreshing ETABS view (RefreshWindow + RefreshView)");
    Model.View.RefreshWindow();
    Model.View.RefreshView();
    SpeckleLog.Logger.Information("✅ View refresh completed");
    progress.Report.Log($"✅ View refresh completed");

    state.ReceivedObjects = newPlaceholderObjects;

    return state;
  }

  private List<ApplicationObject> ConvertReceivedObjects(ISpeckleConverter converter, ProgressViewModel progress)
  {
    List<ApplicationObject> conversionResults = new();
    ConcurrentDictionary<string, int> conversionProgressDict = new() { ["Conversion"] = 1 };

    SpeckleLog.Logger.Information("🔄 Starting conversion of {Count} objects", Preview.Count);

    int objectIndex = 0;
    foreach (var obj in Preview)
    {
      if (!StoredObjects.ContainsKey(obj.OriginalId))
      {
        SpeckleLog.Logger.Warning("⚠️ Object not in StoredObjects: {Id}", obj.OriginalId);
        continue;
      }

      progress.CancellationToken.ThrowIfCancellationRequested();

      var @base = StoredObjects[obj.OriginalId];

      // Log details for first 3 objects to avoid log spam
      if (objectIndex < 3)
      {
        SpeckleLog.Logger.Information("🔹 Converting object #{Index}:", objectIndex);
        SpeckleLog.Logger.Information("   speckle_type: {SpeckleType}", @base.speckle_type);
        SpeckleLog.Logger.Information("   .NET Type: {DotNetType}", @base.GetType().FullName);
        SpeckleLog.Logger.Information("   ID: {Id}", @base.id);
      }

      progress.Report.Log($"🔹 Type: {@base.speckle_type} | ID: {@base.id}");

      objectIndex++;

      if (@base is Element2D e2d)
      {
        progress.Report.Log($"🧱 Element2D found: {e2d.name}");
      }


      using var _0 = LogContext.PushProperty("fromType", @base.GetType());

      try
      {
        progress.Report.Log($"🔍 Converting: {@base.speckle_type} | ID: {@base.id}");

        if (@base is Objects.Structural.Geometry.Element2D elem)
        {
          progress.Report.Log($"🧱 Element2D detected: {elem.name}");
        }

        if (objectIndex - 1 < 3)
        {
          SpeckleLog.Logger.Information("⏳ Calling converter.ConvertToNative()...");
        }
        var conversionResult = (ApplicationObject)converter.ConvertToNative(@base);
        if (objectIndex - 1 < 3)
        {
          SpeckleLog.Logger.Information("✅ ConvertToNative() returned");
        }

        if (objectIndex - 1 < 3)
        {
          SpeckleLog.Logger.Information("🔍 Conversion result - Status: {Status}", conversionResult.Status);
          SpeckleLog.Logger.Information("🔍 Created IDs count: {Count}", conversionResult.CreatedIds?.Count ?? 0);
          SpeckleLog.Logger.Information("🔍 Converted count: {Count}", conversionResult.Converted?.Count ?? 0);

          if (conversionResult.CreatedIds != null && conversionResult.CreatedIds.Any())
          {
            var idStrings = conversionResult.CreatedIds.Select(id => id?.ToString() ?? "null");
            SpeckleLog.Logger.Information("✅ Created IDs: {Ids}", string.Join(", ", idStrings));
          }

          if (conversionResult.Converted != null && conversionResult.Converted.Any())
          {
            var convertedStrings = conversionResult.Converted.Select(c => c?.ToString() ?? "null");
            SpeckleLog.Logger.Information("✅ Converted objects: {Objects}", string.Join(", ", convertedStrings));
          }

          if (conversionResult.Log != null && conversionResult.Log.Any())
          {
            SpeckleLog.Logger.Information("📝 Conversion log: {Log}", string.Join(", ", conversionResult.Log));
          }
        }

        progress.Report.Log($"🔍 Conversion result - Status: {conversionResult.Status}");
        progress.Report.Log($"🔍 Created IDs count: {conversionResult.CreatedIds?.Count ?? 0}");
        progress.Report.Log($"🔍 Converted count: {conversionResult.Converted?.Count ?? 0}");

        if (conversionResult.CreatedIds != null && conversionResult.CreatedIds.Any())
        {
          var idStrings = conversionResult.CreatedIds.Select(id => id?.ToString() ?? "null");
          progress.Report.Log($"✅ Created IDs: {string.Join(", ", idStrings)}");
        }

        if (conversionResult.Converted != null && conversionResult.Converted.Any())
        {
          var convertedStrings = conversionResult.Converted.Select(c => c?.ToString() ?? "null");
          progress.Report.Log($"✅ Converted objects: {string.Join(", ", convertedStrings)}");
        }

        if (conversionResult.Log != null && conversionResult.Log.Any())
        {
          progress.Report.Log($"📝 Conversion log: {string.Join(", ", conversionResult.Log)}");
        }

        var finalStatus =
          conversionResult.Status != ApplicationObject.State.Unknown
            ? conversionResult.Status
            : ApplicationObject.State.Created;

        if (objectIndex - 1 < 3)
        {
          SpeckleLog.Logger.Information("📊 Final status: {Status}", finalStatus);
        }
        progress.Report.Log($"📊 Final status: {finalStatus}");

        obj.Update(
          status: finalStatus,
          createdIds: conversionResult.CreatedIds,
          converted: conversionResult.Converted,
          log: conversionResult.Log
        );
      }
      catch (Exception ex) when (!ex.IsFatal())
      {
        ConnectorHelpers.LogConversionException(ex);

        var failureStatus = ConnectorHelpers.GetAppObjectFailureState(ex);
        obj.Update(status: failureStatus, logItem: ex.Message);
      }

      conversionResults.Add(obj);

      progress.Report.UpdateReportObject(obj);

      conversionProgressDict["Conversion"]++;
      progress.Update(conversionProgressDict);
    }

    if (converter is IFinalizable finalizable)
    {
      finalizable.FinalizeConversion();
    }

    return conversionResults;
  }

  /// <summary>
  /// Traverses the object graph, returning objects to be converted.
  /// </summary>
  /// <param name="obj">The root <see cref="Base"/> object to traverse</param>
  /// <param name="converter">The converter instance, used to define what objects are convertable</param>
  /// <returns>A flattened list of objects to be converted ToNative</returns>
  private List<ApplicationObject> FlattenCommitObject(Base obj, ISpeckleConverter converter, Action<string> log)
  {
    void StoreObject(Base b)
    {
      if (!StoredObjects.ContainsKey(b.id))
      {
        StoredObjects.Add(b.id, b);
      }
    }

    ApplicationObject CreateApplicationObject(Base current)
    {
      log($"=================\nobject.GetType: {current.GetType()}\nobject.speckle_type: {current.speckle_type}");
      ApplicationObject NewAppObj()
      {
        var speckleType = current
          .speckle_type.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
          .LastOrDefault();

        return new ApplicationObject(current.id, speckleType) { applicationId = current.applicationId, };
      }

      //Handle convertable objects
      if (converter.CanConvertToNative(current))
      {
        var appObj = NewAppObj();
        appObj.Convertible = true;

        log($"✅ Will convert: {current.speckle_type} | ID: {current.id}");

        if (current is Element2D elem2D)
          log($"🧱 Found Element2D (Wall/Slab): {elem2D.name} | ID: {elem2D.id}");

        StoreObject(current);
        return appObj;
      }



      //Handle objects convertable using displayValues
      var fallbackMember = DefaultTraversal
        .DisplayValuePropAliases.Where(o => current[o] != null)
        .Select(o => current[o])
        .FirstOrDefault();

      if (fallbackMember != null)
      {
        var appObj = NewAppObj();
        var fallbackObjects = GraphTraversal.TraverseMember(fallbackMember).Select(CreateApplicationObject);
        appObj.Fallback.AddRange(fallbackObjects);

        StoreObject(current);
        return appObj;
      }
      log($"❌ Skipped object: {current.speckle_type} | ID: {current.id}");
      return null;
    }

    var traverseFunction = DefaultTraversal.CreateTraversalFunc();

    var objectsToConvert = traverseFunction
      .Traverse(obj)
      .Select(tc => CreateApplicationObject(tc.current))
      .Where(appObject => appObject != null)
      .Reverse() //just for the sake of matching the previous behaviour as close as possible
      .ToList();

    log($"🔍 Total objects queued for conversion: {objectsToConvert.Count}");


    return objectsToConvert;
  }

  private void RefreshDatabaseTable(string floorTableKey)
  {
    int tableVersion = 0;
    int numberRecords = 0;
    string[] fieldsKeysIncluded = null;
    string[] tableData = null;
    int numFatalErrors = 0;
    int numWarnMsgs = 0;
    int numInfoMsgs = 0;
    int numErrorMsgs = 0;
    string importLog = "";
    Model.DatabaseTables.GetTableForEditingArray(
      floorTableKey,
      "ThisParamIsNotActiveYet",
      ref tableVersion,
      ref fieldsKeysIncluded,
      ref numberRecords,
      ref tableData
    );

    double version = 0;
    string versionString = null;
    Model.GetVersion(ref versionString, ref version);
    var programVersion = versionString;

    // this is a workaround for a CSI bug. The applyEditedTables is looking for "Unique Name", not "UniqueName"
    // this bug is patched in version 20.0.0
    if (programVersion.CompareTo("20.0.0") < 0 && fieldsKeysIncluded[0] == "UniqueName")
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
    Model.DatabaseTables.ApplyEditedTables(
      false,
      ref numFatalErrors,
      ref numErrorMsgs,
      ref numWarnMsgs,
      ref numInfoMsgs,
      ref importLog
    );
  }

  // delete previously sent objects that are no longer in this stream
  private void DeleteObjects(
    IReadOnlyCollection<ApplicationObject> previouslyReceiveObjects,
    IReadOnlyCollection<ApplicationObject> newPlaceholderObjects,
    ProgressViewModel progress
  )
  {
    foreach (var obj in previouslyReceiveObjects)
    {
      if (obj.Converted.Count == 0)
      {
        continue;
      }

      if (newPlaceholderObjects.Any(x => x.applicationId == obj.applicationId))
      {
        continue;
      }

      foreach (var o in obj.Converted)
      {
        if (o is not string s)
        {
          continue;
        }

        string[] typeAndName = s.Split(new[] { ConnectorCSIUtils.Delimiter }, StringSplitOptions.None);
        if (typeAndName.Length != 2)
        {
          continue;
        }

        switch (typeAndName[0])
        {
          case "Frame":
            Model.FrameObj.Delete(typeAndName[1]);
            break;
          case "Area":
            Model.AreaObj.Delete(typeAndName[1]);
            break;
          default:
            continue;
        }

        obj.Update(status: ApplicationObject.State.Removed);
        progress.Report.Log(obj);
      }
    }
  }
}
