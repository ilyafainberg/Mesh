using Mesh.BuiltIns.Compiler;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Mesh.BuiltIns.Compiler <BuiltIns directory> <builtins.index.json>");
    return 2;
}

try
{
    var catalog = BuiltInContentCompiler.Compile(args[0], args[1]);
    Console.WriteLine($"Built-in content: {catalog.Items.Count} items, version {catalog.ContentVersion}, hash {catalog.CatalogHash}.");
    return 0;
}
catch (BuiltInCompilationException ex)
{
    Console.Error.WriteLine("Built-in content validation failed:");
    foreach (var error in ex.Errors) Console.Error.WriteLine("- " + error);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Built-in content compilation failed: " + ex.Message);
    return 1;
}
