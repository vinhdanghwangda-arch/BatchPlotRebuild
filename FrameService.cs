public static void AddNumbers(
    Document doc, List<Frame> ordered)
{
    ClearNumbers(doc);

    if (ordered == null || ordered.Count == 0)
        return;

    Database db = doc.Database;

    // Use the smallest frame height for consistent number sizing.
    double minHeight = ordered
        .Where(f => f != null && f.H > 0)
        .Select(f => f.H)
        .DefaultIfEmpty(0)
        .Min();

    if (minHeight <= 0)
        return;

    double textHeight = minHeight * 2.0 / 3.0;

    using (var tr =
        db.TransactionManager.StartTransaction())
    {
        var lt = (LayerTable)tr.GetObject(
            db.LayerTableId, OpenMode.ForRead);

        if (!lt.Has(SttLayer))
        {
            lt.UpgradeOpen();

            var ltr = new LayerTableRecord
            {
                Name = SttLayer,
                IsPlottable = false,
                Color = Color.FromColorIndex(
                    ColorMethod.ByAci, 1)
            };

            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        var ms = (BlockTableRecord)tr.GetObject(
            db.CurrentSpaceId, OpenMode.ForWrite);

        int n = 0;

        foreach (Frame f in ordered)
        {
            if (f == null || f.W <= 0 || f.H <= 0)
                continue;

            n++;

            // Draw a diagonal from bottom-left to top-right.
            var diagonal = new Line(
                new Point3d(f.MinX, f.MinY, 0),
                new Point3d(f.MaxX, f.MaxY, 0));

            diagonal.SetDatabaseDefaults();
            diagonal.Layer = SttLayer;

            ms.AppendEntity(diagonal);
            tr.AddNewlyCreatedDBObject(diagonal, true);

            // Place the drawing number at the frame center.
            var center = new Point3d(
                (f.MinX + f.MaxX) / 2.0,
                (f.MinY + f.MaxY) / 2.0,
                0);

            var t = new DBText();
            t.SetDatabaseDefaults();
            t.Layer = SttLayer;
            t.TextString = n.ToString();
            t.Height = textHeight;
            t.Position = center;
            t.HorizontalMode = TextHorizontalMode.TextCenter;
            t.VerticalMode = TextVerticalMode.TextVerticalMid;
            t.AlignmentPoint = center;

            ms.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
        }

        tr.Commit();
    }
}

public static int ClearNumbers(Document doc)
{
    int count = 0;
    Database db = doc.Database;

    using (var tr =
        db.TransactionManager.StartTransaction())
    {
        var ms = (BlockTableRecord)tr.GetObject(
            db.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId id in ms)
        {
            var ent = tr.GetObject(
                id, OpenMode.ForRead) as Entity;

            if (ent == null ||
                !string.Equals(
                    ent.Layer,
                    SttLayer,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!(ent is DBText) && !(ent is Line))
                continue;

            ent.UpgradeOpen();
            ent.Erase();
            count++;
        }

        tr.Commit();
    }

    return count;
}
