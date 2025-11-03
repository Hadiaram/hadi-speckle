# ETABS22 Receive Diagnostic Guide

## Issue
Objects can be received from Speckle into ETABS22 without errors, but they don't appear in the model.

## Recent Changes
Added comprehensive diagnostic logging to identify where the receive process is failing.

## How to Test with New Diagnostics

### Step 1: Rebuild with Latest Code
```cmd
git pull origin claude/etabs22-support-011CURhHYMJPk3Fq9Bb69KVU
build-etabs22.bat
```

### Step 2: Close and Restart ETABS22
**Important:** Close ETABS completely and restart to load the new DLL.

### Step 3: Attempt to Receive Objects
Try receiving objects from Speckle and watch the logs carefully.

## What to Look For in the Logs

### 1. Converter Initialization
You should see:
```
✅ Using direct converter reference for ETABS22 receive
✅ Created ConverterCSI instance for receive
🔍 Converter type: Objects.Converter.CSI.ConverterCSI
```

If you see "Using default kit manager" instead, the code isn't compiled correctly.

### 2. Traversal Results
```
🔍 Objects returned by traversal: X
```

If this number is 0, no objects are being identified as convertible.

### 3. Object Discovery
For each object found:
```
✅ Will convert: Objects.Structural.Geometry.Element1D | ID: abc123
```

If you see "❌ Skipped object" instead, the converter doesn't know how to handle that type.

### 4. Conversion Results
For each object being converted, you should see:
```
🔍 Converting: Objects.Structural.Geometry.Element1D | ID: abc123
🔍 Conversion result - Status: Created (or Updated/Failed)
🔍 Created IDs count: 1
🔍 Converted count: 1
✅ Created IDs: Frame:1
✅ Converted objects: Frame:1
📊 Final status: Created
```

**Key indicators:**
- **Status**: Should be `Created` or `Updated`, not `Failed` or `Unknown`
- **Created IDs count**: Should be > 0 if object was successfully created
- **Converted count**: Should be > 0 if object was successfully converted
- If these are 0, the converter is not creating objects in ETABS

### 5. Conversion Summary
After all objects are processed:
```
📊 Conversion Summary:
   Total objects processed: 5
   Created: 5
   Updated: 0
   Failed: 0
   Skipped: 0
```

**What this tells you:**
- **Created > 0**: Objects were successfully created in ETABS API
- **Failed > 0**: Objects failed to convert - check error logs
- **Skipped > 0**: Objects weren't convertible - might be wrong type

### 6. View Refresh
```
🔄 Refreshing ETABS view (RefreshWindow + RefreshView)
✅ View refresh completed
```

This confirms the view refresh was called. If objects were created but still not visible, the issue is likely a display/refresh bug in ETABS.

## Possible Issues and What Logs Will Show

### Issue: Objects Not Convertible
**Symptoms:**
```
❌ Skipped object: Objects.Other.Point | ID: xyz789
🔍 Total objects queued for conversion: 0
```

**Cause:** The converter doesn't support this object type for ETABS22.

**Solution:** Check what object types you're trying to receive. Only structural elements (beams, columns, walls, slabs) are typically supported.

### Issue: Conversion Failing
**Symptoms:**
```
🔍 Conversion result - Status: Failed
🔍 Created IDs count: 0
📊 Conversion Summary:
   Failed: 5
```

**Cause:** Converter encountered an error during conversion. Look for exception messages in the logs.

**Solution:** Check the specific error message. Common issues:
- Invalid geometry
- Missing required properties
- ETABS API errors

### Issue: Objects Created But Not Visible
**Symptoms:**
```
🔍 Conversion result - Status: Created
🔍 Created IDs count: 1
✅ Created IDs: Frame:123
📊 Conversion Summary:
   Created: 5
🔄 Refreshing ETABS view
✅ View refresh completed
```

**Cause:** Objects are successfully created in ETABS API, but display isn't updating.

**Possible Solutions:**
1. Manually refresh view in ETABS (View > Refresh View)
2. Check if objects are on a hidden layer
3. Check if objects are outside current view bounds
4. Try zooming to fit (View > Zoom > Fit All)

### Issue: Old Code Still Running
**Symptoms:**
```
✅ Using default kit manager for receive
System.InvalidOperationException: Sequence contains no matching element
```

**Cause:** You're still running the old code that uses KitManager.

**Solution:**
1. Confirm you pulled the latest code
2. Clean and rebuild: `rmdir /s /q ConnectorCSI\ConnectorETABS22\bin`
3. Close ETABS completely
4. Rebuild: `build-etabs22.bat`
5. Restart ETABS

### Issue: Line Conversion Failures - RESOLVED
**Root Cause:** LineToNative was missing the required section property parameter in AddByCoord call.

**Symptoms:**
```
🔍 LineToNative called:
   Start: (0, 0, 8.624620826666666) mm
   End: (3.107, 0, 8.624666666666666) mm
   Model units: inch
   Converted start: (0, 0, 0.33955218440794926)
   Converted end: (0.1223229007, 0, 0.3395539891333333)
   Frame length: 0.122323 inch
   API result: success=1, frame=
```

**Analysis:**
- Error code 1 = Invalid operation
- Frame length (0.122 inches) is valid - not too short
- Coordinates are valid - no NaN or Infinity
- **Issue**: AddByCoord was called with 7 parameters, but ETABS requires 8 parameters including section property type

**Fix Applied:**
Changed from:
```csharp
Model.FrameObj.AddByCoord(x1, y1, z1, x2, y2, z2, ref newFrame);
```

To:
```csharp
Model.FrameObj.AddByCoord(x1, y1, z1, x2, y2, z2, ref newFrame, "Default");
```

This matches the pattern used in ConvertFrame.cs (CreateFrame method). ETABS requires a section property to be specified - using "Default" tells ETABS to assign the default frame section property to the new frame.

## Next Steps

After running receive with the new diagnostics, share the full log output. The enhanced logging will help identify exactly where the process is failing:

1. Is the converter being loaded correctly?
2. Are objects being identified as convertible?
3. Is the conversion succeeding?
4. Are objects being created in the ETABS API?
5. Is the view refresh working?
6. **For Line objects**: What error code and coordinates are being logged?

With this information, we can pinpoint the exact issue and apply the right fix.
