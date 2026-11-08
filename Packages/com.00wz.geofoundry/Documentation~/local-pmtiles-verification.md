# PMTiles validation report

Validated on 8 November 2026 with Unity 6000.4.8f1. Temporary probe projects,
scripts, generated datasets, logs and builds were removed after validation.
The maintained reader/source tests are in `Tests/EditMode/TileIO/PmTilesTests.cs`.

## Hypotheses and decisions

| Hypothesis | Experiment and result | Decision |
| --- | --- | --- |
| Unity SimpleWebServer supports Range | curl against the installed binary and the user's actual WebGL Build and Run both returned 200, Content-Length 256, no Content-Range for bytes=17-32. Byte identity and source/build SHA256 matched. Accept-Ranges was misleading | WebGL hosting requires a different server with real 206; development server included |
| No usable C# reader exists | GitHub/NuGet/OpenUPM research found MIT PMTiles.NET (net6/7/8, HttpClient) and maplibre-unity PMTilesArchive (Unity API, but different cancellation/limits). Neither was adopted without substantial adaptation | Own bounded C# reader over IByteRangeSource; no native dependencies |
| Gzip/Deflate work in IL2CPP WebGL | User's WebGL test decoded and checked 4 MiB: gzip PASS 13.61 ms, deflate PASS 9.94 ms. Time includes byte verification. New source integration also decoded gzip PMTiles | GZipStream; no Burst inflate dependency |
| Full-file loading is viable for the intended archive | 8/16 MiB succeeded, larger full downloads sometimes hung. Inspecting generated Unity JS found full-size JS buffer reservation from Content-Length before C# callbacks. Exact hang cause and universal limit were not established | No whole-archive fallback for the intended approximately 1 GB dataset. Web transport rejects 200 at headers through a bounded JS fetch adapter |
| Android can read StreamingAssets inside APK | User's APK test logged DownloadHandlerFile result=Success and random Seek PASS after extraction | Extract once per source instance to persistentDataPath, then use filesystem ranges; production lifecycle remains a device check |

Reader research:
[PMTiles.NET](https://github.com/yossy4411/PMTiles.NET), examined commit
`27e4718b0030ee7f2cbb7969130614c58defc121`;
[maplibre-unity](https://github.com/KazukiKuriyama/maplibre-unity), examined commit
`9694766b4bdfbbd4bb217d8bf16d70f73891d188`.
No exhaustive absence claim is made. A browser File/Blob alternative was tested
during research but excluded at the user's request; no picker is implemented.

## Implementation verification

**25 EditMode tests passed, zero failed**, in an isolated minimal Unity project
containing the current Core/TileIO sources and PMTiles tests. This was not a run
of the complete GeoFoundry suite. Tests generate synthetic MVT/PNG archives:
root/leaf, None/Gzip, XYZ/Hilbert reference values, cache reuse, run lengths,
contiguous/deduplicated offsets, missing/empty DEM, invalid headers/varints/gzip,
unsupported compression/version, cancellation, size limits, ownership, texture
release, disposal and actual FileStream reads. No third-party map fixtures.

A new **IL2CPP WebGL development build** was built and run automatically in
headless Microsoft Edge 157 on Windows. It used the package's development server
for the build and PMTiles fixtures; controlled slow/Range-ignoring endpoints
were supplied by the test wrapper. Console evidence:

```text
GF_PM_SMOKE START 6000.4.8f1 WebGLPlayer
GF_PM_SMOKE MVT+leaf+gzip PASS
GF_PM_SMOKE DEM+PNG+missing PASS
GF_PM_SMOKE 1 GiB archive tail range PASS
GF_PM_SMOKE ignored Range rejected PASS
GF_PM_SMOKE cancellation PASS
GF_PM_SMOKE PASS
```

The large-file case read bytes 1073676288-1073741823 from a synthetic file of
1073741824 bytes through the production JS/C# HttpByteRangeSource. The server
sent exactly 65536 bytes; contents and total length were checked. This is
evidence for large offsets and bounded transport, not for Moscow's actual data.
The final request after cancellation also succeeded.

Additional automated Node checks passed for the actual `.jslib`: exact bytes,
length reporting, repeated reads, rejection of HTTP 200 and cancellation. After
Release there were zero retained JS request handles and zero open server
responses. Server checks passed for explicit/suffix/open-ended ranges, HEAD,
416 responses, CORS, methods, root traversal and empty files. These temporary
checks were removed along with the experimental harness at the user's request.

The tests do not establish a universal memory budget or prove absence of every
possible leak in long-running application sessions. No whole-city rendering or
performance benchmark was performed.

## Still requires validation

- Actual Moscow MVT/DEM archives: schema, coverage, elevations and nodata.
- Production Android extraction/dispose/cancellation: the earlier successful
  extraction/Seek was a prototype APK test, not a device run of the final sources.
- iOS devices, Safari/mobile Web and low-memory hardware.
- The full working scene with both real archives, long-running load/cancel stress.
- Production CORS, HTTPS/reverse proxy and archive-update lifecycle.

See [local-pmtiles.md](local-pmtiles.md) for deployment requirements, source setup,
the included server and a manual checklist.
