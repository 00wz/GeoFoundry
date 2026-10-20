using System.Runtime.CompilerServices;

// Exposes internal types (Burst jobs, helpers) to the package's test assemblies only;
// production consumers of the package do not see them.
[assembly: InternalsVisibleTo("GeoFoundry.Tests.EditMode")]
[assembly: InternalsVisibleTo("GeoFoundry.Tests.PlayMode")]
