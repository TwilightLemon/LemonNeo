using System.Net;
using System.Text.Json;
using LemonNeo.Core.Exceptions;
using Xunit;

namespace LemonNeo.Plugin.QqMusic.Tests;

public sealed class QqMusicApiClientTests
{
    private static readonly QqMusicCredentials Authentication = new("test-cookie", "1234", 5381);

    [Fact]
    public async Task ReadsNativeProfileCountsAndCoversWithoutFetchingDetails()
    {
        var requests = 0;
        using var http = CreateClient(_ =>
        {
            requests++;
            return Json("""
                {"code":0,"data":{
                  "creator":{"nick":"Test user"},
                  "mymusic":[{"type":1,"id":"100","title":"Liked","num0":788,"picurl":"//y.gtimg.cn/liked.jpg"}],
                  "mydiss":{"list":[
                    {"dissid":200,"title":"First","picurl":"http://y.gtimg.cn/created.jpg"},
                    {"dissid":"300","title":"Second","songnum":0}
                  ]}
                }}
                """);
        });

        var profile = await new QqMusicApiClient(http).GetProfileAsync(Authentication, default);

        Assert.Equal(1, requests);
        Assert.Equal(["playlist:100", "playlist:200", "playlist:300"], profile.CreatedPlaylists.Select(p => p.Id));
        Assert.Equal([788, null, 0], profile.CreatedPlaylists.Select(p => p.TrackCount));
        Assert.Equal("https://y.gtimg.cn/liked.jpg", profile.CreatedPlaylists[0].ArtworkUri?.AbsoluteUri);
        Assert.Equal("https://y.gtimg.cn/created.jpg", profile.CreatedPlaylists[1].ArtworkUri?.AbsoluteUri);
        Assert.Null(profile.CreatedPlaylists[2].ArtworkUri);
    }

    [Fact]
    public async Task ReadsFollowedPlaylistCover()
    {
        using var http = CreateClient(_ => Json("""
            {"code":0,"data":{"cdlist":[{"dissid":200,"dissname":"Followed","songnum":12,"logo":"//y.gtimg.cn/followed.jpg"}]}}
            """));

        var playlists = await new QqMusicApiClient(http).GetFollowedPlaylistsAsync(Authentication, default);

        var playlist = Assert.Single(playlists);
        Assert.Equal(12, playlist.TrackCount);
        Assert.Equal("https://y.gtimg.cn/followed.jpg", playlist.ArtworkUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ListsFollowedAlbumsUsingAccountParametersAndNativeMetadata()
    {
        using var http = CreateClient(request =>
        {
            Assert.Equal("c.y.qq.com", request.RequestUri!.Host);
            Assert.Contains("reqtype=2", request.RequestUri.Query);
            Assert.Contains("userid=1234", request.RequestUri.Query);
            Assert.Contains("loginUin=1234", request.RequestUri.Query);
            Assert.Contains("g_tk=5381", request.RequestUri.Query);
            Assert.Contains("sin=0&ein=89", request.RequestUri.Query);
            Assert.Equal(Authentication.Cookie, Assert.Single(request.Headers.GetValues("Cookie")));
            return Json("""
                {"code":0,"data":{"has_more":0,"albumlist":[
                  {"albummid":"AlbumA","albumname":"Followed","songnum":"14","pic":"//y.gtimg.cn/album.jpg"},
                  {"albummid":"AlbumB","albumname":"No count"},
                  {"albummid":"AlbumA","albumname":"Duplicate"},
                  {"albumname":"No identifier"}
                ]}}
                """);
        });

        var albums = await new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, default);

        Assert.Equal(["album:AlbumA", "album:AlbumB"], albums.Select(a => a.Id));
        Assert.Equal(["Followed", "No count"], albums.Select(a => a.Name));
        Assert.Equal([14, null], albums.Select(a => a.TrackCount));
        Assert.Equal("https://y.gtimg.cn/album.jpg", albums[0].ArtworkUri?.AbsoluteUri);
        Assert.EndsWith("T002R500x500M000AlbumB.jpg", albums[1].ArtworkUri?.AbsoluteUri);
    }

    [Fact]
    public async Task PaginatesFollowedAlbumsAndDeduplicatesAcrossPages()
    {
        var requests = 0;
        using var http = CreateClient(request =>
        {
            requests++;
            if (requests == 1)
            {
                Assert.Contains("sin=0&ein=89", request.RequestUri!.Query);
                return AlbumPage(Enumerable.Range(0, 90), hasMore: 1);
            }

            Assert.Contains("sin=90&ein=179", request.RequestUri!.Query);
            return AlbumPage([89, 90, 91], hasMore: 0);
        });

        var albums = await new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, default);

        Assert.Equal(2, requests);
        Assert.Equal(92, albums.Count);
        Assert.Equal(Enumerable.Range(0, 92).Select(id => $"album:Album{id}"), albums.Select(a => a.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopsOnLastOrRepeatedFullPage(bool repeatPage)
    {
        var requests = 0;
        using var http = CreateClient(_ =>
        {
            requests++;
            Assert.True(requests <= 2, "Pagination should stop when the server repeats a page.");
            return AlbumPage(Enumerable.Range(0, 90), hasMore: repeatPage ? 1 : 0);
        });

        var albums = await new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, default);

        Assert.Equal(repeatPage ? 2 : 1, requests);
        Assert.Equal(90, albums.Count);
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"albumlist\":[]}}")]
    [InlineData("{\"code\":0,\"data\":{}}")]
    public async Task HandlesEmptyFollowedAlbums(string json)
    {
        using var http = CreateClient(_ => Json(json));
        Assert.Empty(await new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, default));
    }

    [Fact]
    public async Task ReportsFollowedAlbumApiFailure()
    {
        using var http = CreateClient(_ => Json("""{"code":1000}"""));
        await Assert.ThrowsAsync<ProviderException>(() =>
            new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, default));
    }

    [Fact]
    public async Task KeepsPurchasedCountUnknownWithoutFetchingAlbumDetails()
    {
        var requests = 0;
        using var http = CreateClient(_ =>
        {
            requests++;
            return Json("""
                {"code":0,"data":{"albumlist":[
                  {"albummid":"AlbumA","album_name":"Purchased"},
                  {"albummid":"AlbumB","album_name":"Known","song_cnt":"12"}
                ]}}
                """);
        });

        var albums = await new QqMusicApiClient(http).GetPurchasedAlbumsAsync(Authentication, default);

        Assert.Equal(1, requests);
        Assert.Equal([null, 12], albums.Select(a => a.TrackCount));
        Assert.All(albums, album => Assert.NotNull(album.ArtworkUri));
        Assert.EndsWith("T002R500x500M000AlbumA.jpg", albums[0].ArtworkUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ImportsFollowedAlbumThroughExistingAlbumDetailEndpoint()
    {
        using var http = CreateClient(request =>
        {
            Assert.Contains("albummid=AlbumA", request.RequestUri!.Query);
            Assert.Contains("song_num=999", request.RequestUri.Query);
            return Json("""{"code":0,"data":{"name":"Album","list":[{"songmid":"TrackA","songname":"Track"}]}}""");
        });

        var album = await new QqMusicApiClient(http).GetAlbumAsync(Authentication, "AlbumA", default);

        Assert.NotNull(album);
        Assert.Equal("album:AlbumA", album.Id);
        Assert.Equal("Track", Assert.Single(album.Tracks).Title);
    }

    [Fact]
    public async Task HonorsFollowedAlbumCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = CreateClient(_ => throw new InvalidOperationException("Request should be canceled."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QqMusicApiClient(http).GetFollowedAlbumsAsync(Authentication, cancellation.Token));
    }

    private static HttpResponseMessage AlbumPage(IEnumerable<int> ids, int hasMore) => Json(JsonSerializer.Serialize(new
    {
        code = 0,
        data = new
        {
            has_more = hasMore,
            albumlist = ids.Select(id => new { albummid = $"Album{id}", albumname = $"Album {id}", songnum = 1 })
        }
    }));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
