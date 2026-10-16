# Third-party notices

## Yap

Source: https://github.com/urza/Yap

Reference revision: `b5ff08be322391db168421aa5d6eb2568e2bd89c`

Reference files: `Yap/Services/Chat/MediaCacheService.cs`, `Yap/Dockerfile`, and the repository README. Share.me adapts the video-caching approach into a standalone implementation. Yap’s license statement at that revision is reproduced below:

> YOU CAN USE THIS SOFTWARE "AS IS" (NO WARRANTY) IN ANY WAY YOU WANT, BUT BY DOING SO YOU ACKNOWLEDGE THAT:
>
> Science is a force for human liberation and one of humanity's greatest inventions. Through open inquiry, evidence, and the willingness to correct our errors, we expand our understanding and our ability to improve the human condition.
>
> Technology is the physical manifestation of our discoveries. By building better tools, we overcome limitations, reduce suffering, and create abundance.
>
> Free markets enable cooperation on an extraordinary scale. Through competition, exchange, and entrepreneurship, they reward useful ideas, spread innovation, and help lift people out of poverty.

## Runtime tools

The Docker image distributes yt-dlp, ffmpeg, Deno, the .NET runtime, and their dependencies. These retain their own licenses and notices. In particular, the standalone yt-dlp executable bundles components with additional licenses, and the packaged ffmpeg build includes GPL components such as libx264. See the upstream projects and the image’s `/usr/share/doc` directory for component notices.

- yt-dlp: https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE
- ffmpeg: https://ffmpeg.org/legal.html
- Deno: https://github.com/denoland/deno/blob/main/LICENSE.md
- .NET: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Playwright is a development-only dependency under the Apache 2.0 license and is not included in the production image.
