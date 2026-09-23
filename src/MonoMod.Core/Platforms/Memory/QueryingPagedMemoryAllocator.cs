using MonoMod.Utils;
using System;
using System.Diagnostics.CodeAnalysis;

namespace MonoMod.Core.Platforms.Memory
{
    /// <summary>
    /// A base type providing OS methods required for <see cref="QueryingPagedMemoryAllocator"/>.
    /// </summary>
    public abstract class QueryingMemoryPageAllocatorBase
    {
        /// <summary>
        /// Gets the page size.
        /// </summary>
        public abstract uint PageSize { get; }
        /// <summary>
        /// Tries to query the specified page for information.
        /// </summary>
        /// <param name="pageAddr">The address of the page to query.</param>
        /// <param name="isFree"><see langword="true"/> if the page is free; <see langword="false"/> if it is allocated.</param>
        /// <param name="allocBase">The base of the page allocation this page is a part of.</param>
        /// <param name="allocSize">The size of the page allocation this page is a part of.</param>
        /// <returns><see langword="true"/> if the page was successfully queried; <see langword="false"/> otherwise.</returns>
        public abstract bool TryQueryPage(IntPtr pageAddr, out bool isFree, out IntPtr allocBase, out nint allocSize);
        /// <summary>
        /// Tries to allocate a page.
        /// </summary>
        /// <param name="size">The size of the page allocation.</param>
        /// <param name="executable"><see langword="true"/> if the page should be executable; <see langword="false"/> otherwise.</param>
        /// <param name="allocated">The address of the allocated page, if successful.</param>
        /// <returns><see langword="true"/> if a page was successfully allocated; <see langword="false"/> otherwise.</returns>
        public abstract bool TryAllocatePage(nint size, bool executable, out IntPtr allocated);
        /// <summary>
        /// Tries to allocate a specific page.
        /// </summary>
        /// <param name="pageAddr">The address of the page to allocate.</param>
        /// <param name="size">The size of the page allocation.</param>
        /// <param name="executable"><see langword="true"/> if the page should be executable; <see langword="false"/> otherwise.</param>
        /// <param name="allocated">The address of the allocated page, if successful.</param>
        /// <returns><see langword="true"/> if a page was successfully allocated; <see langword="false"/> otherwise.</returns>
        public abstract bool TryAllocatePage(IntPtr pageAddr, nint size, bool executable, out IntPtr allocated);

        /// <summary>
        /// Tries to allocate a page at <paramref name="hint"/>, or as close to it as the OS is willing to place it.
        /// </summary>
        /// <remarks>
        /// Platforms which can place a mapping near an address without first proving that address is free implement
        /// this; the range search uses it to avoid walking the region map (which costs one or two OS calls per region).
        /// Implementations must report the address they actually used, which the caller validates against its bounds.
        /// </remarks>
        public virtual bool TryAllocatePageNear(IntPtr hint, nint size, bool executable, out IntPtr allocated)
            => TryAllocatePage(hint, size, executable, out allocated);
        /// <summary>
        /// Tries to free the page at the provided addresss.
        /// </summary>
        /// <param name="pageAddr">The address of the page to free.</param>
        /// <param name="errorMsg">An error message describing the error that ocurred, if any.</param>
        /// <returns><see langword="true"/> if the page was successfully freed; <see langword="false"/> otherwise.</returns>
        public abstract bool TryFreePage(IntPtr pageAddr, [NotNullWhen(false)] out string? errorMsg);
    }

    /// <summary>
    /// A <see cref="PagedMemoryAllocator"/> built around querying pages in memory.
    /// </summary>
    public sealed class QueryingPagedMemoryAllocator : PagedMemoryAllocator
    {
        private readonly QueryingMemoryPageAllocatorBase pageAlloc;

        /// <summary>
        /// Number of doubling steps (per direction) used when asking the OS to place a page near a target address.
        /// </summary>
        private const int NearProbeSteps = 16;

        /// <summary>
        /// Largest stride used when descending past an unusable gap. The descent doubles up to this and never
        /// gives up, so a required allocation still reaches every 1GB/1MB boundary inside its bounds.
        /// </summary>
        private const int MaxPageStep = 1024 * 1024;


        /// <summary>
        /// The page the last range allocation succeeded from - the next walk starts there when it is still inside
        /// the requested bounds, so a sequence of allocations in the same window does not rescan the map.
        /// </summary>
        private nint lastAllocatedPage;
        /// <summary>
        /// Constructs a <see cref="QueryingPagedMemoryAllocator"/> using the provided <see cref="QueryingMemoryPageAllocatorBase"/>.
        /// </summary>
        /// <param name="alloc">The page allocator to use.</param>
        public QueryingPagedMemoryAllocator(QueryingMemoryPageAllocatorBase alloc)
            : base((nint)Helpers.ThrowIfNull(alloc).PageSize)
        {
            pageAlloc = alloc;
        }

        /// <inheritdoc/>
        protected override bool TryAllocateNewPage(AllocationRequest request, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            if (!pageAlloc.TryAllocatePage(PageSize, request.Executable, out var allocBase))
            {
                allocated = null;
                return false;
            }

            var pageObj = new Page(this, allocBase, (uint)PageSize, request.Executable);
            InsertAllocatedPage(pageObj);

            // now that we have a page, we'll try to allocate out of it
            // if that fails, immediately register for cleanup
            if (!pageObj.TryAllocate((uint)request.Size, (uint)request.Alignment, out var alloc))
            {
                RegisterForCleanup(pageObj);
                allocated = null;
                return false;
            }

            // we successfully allocated, return the page allocation
            allocated = alloc;
            return true;
        }

        /// <inheritdoc/>
        protected override bool TryAllocateNewPage(PositionedAllocationRequest request, nint targetPage, nint lowPageBound, nint highPageBound, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            // we'll do the same approach for trying to find an existing page, but querying the OS for free pages to allocate
            var target = request.Target;

            if (TryAllocateNear(request, targetPage, lowPageBound, highPageBound, out allocated))
                return true;

            var startPage = lastAllocatedPage >= lowPageBound && lastAllocatedPage < highPageBound
                ? lastAllocatedPage
                : targetPage;

            var lowPage = startPage;
            var highPage = startPage + PageSize;

            // Search upwards first. An upward step can skip a whole region or gap in one probe, while a
            // downward step past free space can only advance one page at a time (the query reports where the
            // next region starts, not where free space begins), so an unbounded downward search over a large
            // gap costs one probe per page - hundreds of thousands of OS calls - while the request only needs
            // an address inside the bounds.
            var upwardStep = PageSize;
            while (highPage < highPageBound)
            {
                if (TryAllocNewPage(request, ref highPage, true, ref upwardStep, out allocated))
                    return true;
            }

            // then downwards; this cannot be capped, because for targets whose upper half is fully mapped the
            // free space only exists below and a cap turns a required allocation into a failure
            var downwardStep = PageSize;
            while (lowPage >= lowPageBound)
            {
                if (TryAllocNewPage(request, ref lowPage, false, ref downwardStep, out allocated))
                    return true;
            }

            // if we fall out to here, we just couldn't allocate, so sucks
            allocated = null;
            return false;
        }


        /// <summary>
        /// Asks the OS to place a page at or near the target. One probe is a single OS call, while the region walk
        /// below costs one or two per region it crosses - seconds on a fragmented address space, which is what a
        /// translated (Rosetta) macOS process has.
        /// </summary>
        private bool TryAllocateNear(PositionedAllocationRequest request, nint targetPage, nint lowPageBound, nint highPageBound, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            for (var step = 0; step < NearProbeSteps; step++)
            {
                var offset = (nint)1 << step;

                for (var direction = 0; direction < 2; direction++)
                {
                    var candidate = direction == 0 ? targetPage + offset * PageSize : targetPage - offset * PageSize;
                    if (candidate < lowPageBound || candidate >= highPageBound)
                        continue;
                    if (!pageAlloc.TryAllocatePageNear(candidate, PageSize, request.Base.Executable, out var address))
                        continue;

                    // the OS may place it elsewhere; only an address inside the bounds is acceptable
                    if (address < lowPageBound || address >= highPageBound)
                    {
                        pageAlloc.TryFreePage(address, out _);
                        continue;
                    }

                    var page = new Page(this, address, (uint)PageSize, request.Base.Executable);
                    InsertAllocatedPage(page);

                    if (!page.TryAllocate((uint)request.Base.Size, (uint)request.Base.Alignment, out var alloc))
                    {
                        RegisterForCleanup(page);
                        continue;
                    }

                    allocated = alloc;
                    return true;
                }
            }

            allocated = null;
            return false;
        }

        private unsafe bool TryAllocNewPage(PositionedAllocationRequest request, ref nint page, bool goingUp, ref nint pageStep, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            if (pageAlloc.TryQueryPage(page, out var isFree, out var baseAddr, out var allocSize))
            {
                if (!isFree)
                {
                    // stepping past a mapped region: resume probing at page granularity below it
                    pageStep = PageSize;
                    goto Fail;
                }

                if (!pageAlloc.TryAllocatePage(page, PageSize, request.Base.Executable, out var allocBase)) // allocation failed
                    goto Fail;

                var pageObj = new Page(this, allocBase, (uint)PageSize, request.Base.Executable);
                InsertAllocatedPage(pageObj);

                // now that we have a page, we'll try to allocate out of it
                // if that fails, immediately register for cleanup
                if (!pageObj.TryAllocate((uint)request.Base.Size, (uint)request.Base.Alignment, out var alloc))
                {
                    RegisterForCleanup(pageObj);
                    goto Fail;
                }

                if ((nint)alloc.BaseAddress < request.LowBound || (nint)alloc.BaseAddress >= request.HighBound)
                {
                    MMDbgLog.Error($"Got allocation request at {request.Target:X} within range [{request.LowBound:X}, {request.HighBound:X}) but received out-of-bounds allocation {alloc.BaseAddress:X} within page {pageObj.BaseAddr:X} (size: {pageObj.Size}). TryQueryPage gave baseAddr: {baseAddr:X}");
                    alloc.Dispose();
                    RegisterForCleanup(pageObj);
                    goto Fail;
                }

                // we successfully allocated, return the page allocation
                lastAllocatedPage = pageObj.BaseAddr;
                allocated = alloc;
                return true;

                Fail:
                // We're failing out, update the page address appropriately
                if (goingUp)
                {
                    // an upward step learns where the next region starts, so it can skip a whole gap
                    page = baseAddr + allocSize;
                }
                else
                {
                    // downward the query only reports the *next* region, never where free space begins below, so
                    // descend geometrically: stepping one page across a large unusable gap costs hundreds of
                    // thousands of OS calls, and a fixed stride either degrades the same way or skips the range
                    page = baseAddr - pageStep;
                    if (pageStep < MaxPageStep)
                        pageStep *= 2;
                }

                allocated = null;
                return false;
            }
            else
            {
                // TODO: check GetLastError

                // query failed, fail out
                if (goingUp)
                    page += PageSize;
                else
                    page -= PageSize;
                allocated = null;
                return false;
            }
        }

        /// <inheritdoc/>
        protected override bool TryFreePage(Page page, [NotNullWhen(false)] out string? errorMsg)
            => pageAlloc.TryFreePage(page.BaseAddr, out errorMsg);
    }
}
