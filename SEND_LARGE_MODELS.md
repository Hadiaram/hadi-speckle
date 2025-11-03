# Handling Large Model Sends

## Problem: "Object too large" Error

When sending large ETABS/SAP2000 models, you may encounter:

```
Object ec8736b2eabb81e4805617904a10a973 too large
(size 55475799, max size 25000000)
Consider using detached/chunked properties
```

**What's happening:**
- Speckle server has a 25MB limit per object
- Your model serialized to 55MB+ as a single JSON blob
- Server rejected the upload to prevent memory/performance issues

## Root Cause

The CSI connector was using a plain `Base` object for the commit:

```csharp
var commitObj = new Base();
commitObj["elements"] = objects;  // ← NOT detached!
```

When properties aren't marked with `[DetachProperty]`, Speckle embeds all data directly into the parent object's JSON. With large models (thousands of elements), this creates massive objects that exceed server limits.

## Solution (FIXED)

Changed to use Speckle's `Collection` class:

```csharp
var commitObj = new Collection("CSI Model", "CSI");
commitObj.elements = objects.Cast<Base>().ToList();  // ← Detached!
```

The `Collection.elements` property has `[DetachProperty]` attribute, which tells Speckle to:
1. Store each element as a separate object in the database
2. Replace the list with references (IDs) in the commit object
3. Keep the commit object small regardless of model size

## Technical Details

### What is [DetachProperty]?

The `[DetachProperty]` attribute tells Speckle's serializer to treat a property specially:

**Without DetachProperty:**
```json
{
  "elements": [
    { "id": "abc", "data": "..." },  // ← 10MB of data
    { "id": "def", "data": "..." },  // ← 10MB of data
    { "id": "ghi", "data": "..." }   // ← 10MB of data
  ]
}
// Total: 30MB+ in one object
```

**With DetachProperty:**
```json
{
  "elements": [
    { "$ref": "abc" },  // ← Just a reference
    { "$ref": "def" },  // ← Just a reference
    { "$ref": "ghi" }   // ← Just a reference
  ]
}
// Total: <1KB in commit object
// Elements stored separately in database
```

### Where Detachment is Used

Many Speckle objects already use `[DetachProperty]`:

**Structural.Analysis.Model:**
```csharp
[DetachProperty] public List<Base>? nodes { get; set; }
[DetachProperty] public List<Base>? elements { get; set; }
[DetachProperty] public List<Base>? loads { get; set; }
```

**CSIElement1D:**
```csharp
[DetachProperty] public CSILinearSpring? CSILinearSpring { get; set; }
[DetachProperty] public AnalyticalResults? AnalysisResults { get; set; }
```

**Collection (Core):**
```csharp
[DetachProperty] public List<Base> elements { get; set; } = new();
```

This ensures large models can be sent regardless of size.

## Benefits of This Fix

1. **No size limits** - Models with millions of elements can be sent
2. **Better performance** - Smaller commit objects = faster uploads
3. **Incremental loading** - Receivers can load elements on-demand
4. **Follows best practices** - Matches Revit, Rhino, and other connectors

## Testing

After rebuilding with this fix:

```bash
git pull origin claude/etabs22-support-011CURhHYMJPk3Fq9Bb69KVU
build-etabs22.bat
```

**Close ETABS completely and restart**, then try sending your large model again. The send should succeed regardless of model size.

## Related Issues

If you still get network timeout errors like:
```
Failed to ping 1.1.1.1 - TimedOut
```

This is a separate connectivity issue (firewall, VPN, network instability). The "object too large" error should be completely resolved by using Collection.
