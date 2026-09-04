# Copilot Instructions

## General Guidelines
- First general instruction
- Second general instruction

## Configuration Guidelines
- In project documentation, configuration tables should explicitly show the exact container environment-variable/override name, including whether a setting uses a section prefix such as PowerBi__ or a root-level job parameter.
- For Reports Sanity Check, user-facing deployment configuration should expose only parameters users need to control; fixed application behavior and one-time/runtime-image setup details should not be presented as routine user parameters.

## Reports Sanity Check
- Overview and purpose: Document the objectives and goals of the Reports Sanity Check project.
- Architecture and components: Outline the system architecture and key components involved.
- Setup and configuration: Provide detailed instructions for setting up and configuring the project.
- Key workflows: Describe the main workflows and processes within the project.
- Usage: Include guidelines on how to effectively use the project.
- Known limitations or dependencies: List any known limitations or dependencies that users should be aware of.

## Report Summary
All 1 report(s) healthy

Workspace Name: Claims Workspace QA Fabric  
Total: 1  
Passed: 1  
Failed: 0  
Started On: 2026-08-05 09:59:21 UTC  
Completed On: 2026-08-05 10:03:29 UTC  
Run Id: 4c9939a0319140fb8aff1caf1525804f

### Report Details
| Report              | Status | Slowest load | Details                                   |
|---------------------|--------|--------------|-------------------------------------------|
| Components Report    | Passed | 2.5 s       | 3 page(s) OK, 6 bookmark(s) OK           |

### Performance Metrics
- Page lifetime: 203.242 s; context age: 671 ms → 203.913 s; peak browser working set: n/a; peak JS heap: 9.5 MB.

### Phase Breakdown
| Phase                        | Duration | Count | Scope |
|------------------------------|----------|-------|-------|
| Page create                  | 98 ms    | 0     | —     |
| Host navigation              | 102 ms   | 0     | —     |
| Host ready                   | 70 ms    | 0     | —     |
| SDK total                    | 22.94 s  | 0     | —     |
| SDK initial render           | 6.736 s  | 1     | —     |
| SDK bookmark traversal        | 5.728 s  | 6     | —     |
| SDK page traversal           | 1.183 s  | 3     | —     |
| Interaction page discovery    | 12 ms    | 1     | —     |
| Visible page and toggle traversal | 1.007 s | 1   | —     |
| Interaction bookmark discovery | 25 ms    | 6     | —     |
| Source discovery              | 4.49 s   | 0     | Main  |
| Native candidate probing      | 89.431 s | 0     | Main  |
| Interaction bookmark apply    | 202 ms   | 1     | Average Cost View |
| Bookmark-state native probing  | 469 ms   | 0     | Average Cost View |
| Interaction bookmark apply    | 183 ms   | 1     | Total Cost View |
| Bookmark-state native probing  | 73.97 s  | 0     | Total Cost View |
| Interaction bookmark apply    | 624 ms   | 1     | Created Date All Years |
| Bookmark-state native probing  | 87 ms    | 0     | Created Date All Years |
| Interaction bookmark apply    | 645 ms   | 1     | Completed Date All Years |
| Bookmark-state native probing  | 96 ms    | 0     | Completed Date All Years |
| Interaction bookmark apply    | 618 ms   | 1     | Created Date Last Year |
| Bookmark-state native probing  | 100 ms   | 0     | Created Date Last Year |
| Interaction bookmark apply    | 665 ms   | 1     | Completed Date Last Year |
| Bookmark-state native probing  | 131 ms   | 0     | Completed Date Last Year |
| Interaction total             | 179.914 s| 1     | —     |
| Interaction cleanup           | 22 ms    | 0     | —     |

### Resource Samples
- Host ready @ 270 ms: browser n/a, JS 9.5 MB, processes 0 
- After SDK checks @ 23.287 s: browser n/a, JS 9.5 MB, processes 0 
- After interactions @ 203.209 s: browser n/a, JS 9.5 MB, processes 0 
- Before page close @ 203.242 s: browser n/a, JS 9.5 MB, processes 0 

### PBIR Page Classification
- Category [visibility=HiddenInViewMode, pageBinding=Drillthrough, markedFields=1]; 
- Main [visibility=Visible, pageBinding=Drillthrough, markedFields=1]; 
- YoY / MoM [visibility=HiddenInViewMode, pageBinding=none, markedFields=0] 

Landing page 'Main' (order 0) is marked Drillthrough in PBIR and was excluded from actionable destination coverage. Remove its Drill-through filter in Power BI if it is only the landing page.

Sent automatically by Reports Sanity Check.