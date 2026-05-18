import { formatDate, htmlEncode, notifyInfo } from "@serenity-is/corelib/q";
import {
    ArcElement, BarController, BarElement, CategoryScale, Chart, DoughnutController,
    Filler, Legend, LinearScale, LineController, LineElement, PointElement, Tooltip
} from "chart.js";
import daterangepicker from "daterangepicker";
import "daterangepicker/daterangepicker.css";
import jsVectorMap from "jsvectormap";
import "jsvectormap/dist/maps/world";
import "jsvectormap/dist/css/jsvectormap.css";
import moment from "moment";

Chart.register(ArcElement, BarController, BarElement, CategoryScale, DoughnutController, Filler, Legend,
    LineController, LineElement, LinearScale, PointElement, Tooltip);

export interface DashboardPageOptions {
    visitorsByRegion: { [ key: string ]: number }
}

export function DashboardPage(opt: DashboardPageOptions) {
    new daterangepicker(document.querySelector(".daterange"), {
        opens: "left",
        ranges: {
            'Today': [moment(), moment()],
            'Yesterday': [moment().subtract(1, 'days'), moment().subtract(1, 'days')],
            'Last 7 Days': [moment().subtract(6, 'days'), moment()],
            'Last 30 Days': [moment().subtract(29, 'days'), moment()],
            'This Month': [moment().startOf('month'), moment().endOf('month')],
            'Last Month': [moment().subtract(1, 'month').startOf('month'), moment().subtract(1, 'month').endOf('month')]
        },
        startDate: moment().subtract(29, 'days'),
        endDate: moment()
    }, function (start, end) {
        notifyInfo("You chose: " + start.format('MMMM D, YYYY') + ' - ' + end.format('MMMM D, YYYY'));
    });

    new jsVectorMap({
        selector: "#world-map",
        map: "world",
        regionStyle: {
            initial: {
                fill: 'rgba(64, 80, 96, 0.5)',
                "fill-opacity": 1,
                stroke: 'none',
                "stroke-width": 0,
                "stroke-opacity": 1
            }
        },
        visualizeData: {
            scale: ["#72b1dc", "#f3f7ff"],
            values: opt.visitorsByRegion
        },
        onRegionTooltipShow: (_, el, code) => {
            var visitors = opt.visitorsByRegion[code];
            if (visitors != null)
                el.text(el.text(undefined, true) + ': ' + visitors + ' new visitors', true);
        }
    });

    const scaleGrid = {
        color: 'rgba(140, 142, 150, 0.15)',
        borderDash: [8, 4]
    }

    var salesByQuarterChart = new Chart(document.getElementById('orders-by-quarter-chart') as HTMLCanvasElement, {
        type: 'line',
        data: {
            labels: ['2020 Q1', '2020 Q2', '2020 Q3', '2020 Q4', '2021 Q1', '2021 Q2', '2021 Q3', '2021 Q4'],
            datasets: [
                {
                    backgroundColor: 'rgba(75, 192, 192, 0.5)',
                    borderColor: 'rgb(255, 255, 255, 0.7)',
                    borderWidth: 4,
                    label: 'Closed Orders',
                    fill: true,
                    data: [1969, 3597, 1914, 4293, 3795, 5967, 4460, 5713]
                },
                {
                    label: 'All Orders',
                    backgroundColor: 'rgba(54, 162, 235, 0.4)',
                    borderColor: 'rgba(54, 162, 235, 0.2)',
                    borderWidth: 4,
                    fill: true,
                    data: [4912, 3767, 6810, 5670, 4820, 15073, 10687, 8432]
                }
            ]
        },
        options: {
            elements: {
                point: {
                    radius: 0,
                    hitRadius: 6
                }
            },
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: {
                    ticks: {
                        color: 'rgb(140, 142, 150)'
                    },
                    grid: {
                        display: false,
                    }
                },
                y: {
                    ticks: {
                        color: 'rgb(140, 142, 150)'
                    },
                    grid: scaleGrid
                }
            }
        }
    });

    var salesByTypeChart = new Chart(document.getElementById('orders-by-type-chart') as HTMLCanvasElement, {
        type: 'doughnut',
        data: {
            labels: ["Download Sales", "In-Store Sales", "Mail-Order Sales"],
            datasets: [{
                label: 'Sales by Type',
                data: [20, 50, 30],
                backgroundColor: [
                    '#4dc9f6',
                    '#f67019',
                    '#f53794'
                ]
            }]
        },
        options: {
            responsive: false,
            maintainAspectRatio: false
        }
    });

    $('.s-dashboard-card ul.nav a').on('shown.bs.tab', function () {
        salesByQuarterChart.update();
        salesByTypeChart.update();
    });

    new Chart(document.getElementById('traffic-chart') as HTMLCanvasElement, {
        type: 'bar',
        data: {
            labels: Array(37).fill(null).map(function (x, n) {
                var d = new Date(); d.setDate(d.getDate() - n);
                return formatDate(d, 'MMM dd');
            }).reverse(),
            datasets: [
                {
                    label: 'Search',
                    backgroundColor: "#206bc4",
                    barPercentage: 0.7,
                    data: [1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 2, 12, 5, 8, 22, 6, 8, 6, 4, 1, 8, 24, 29, 51, 40, 47, 23, 26, 50, 26, 41, 22, 46, 47, 81, 46, 6]
                },
                {
                    label: 'Social',
                    backgroundColor: "#79a6dc",
                    barPercentage: 0.7,
                    data: [2, 5, 4, 3, 3, 1, 4, 7, 5, 1, 2, 5, 3, 2, 6, 7, 7, 1, 5, 5, 2, 12, 4, 6, 18, 3, 5, 2, 13, 15, 20, 47, 18, 15, 11, 10, 0]
                },
                {
                    label: 'Other',
                    backgroundColor: "#bfe399",
                    barPercentage: 0.7,
                    data: [2, 9, 1, 7, 8, 3, 6, 5, 5, 4, 6, 4, 1, 9, 3, 6, 7, 5, 2, 8, 4, 9, 1, 2, 6, 7, 5, 1, 8, 3, 2, 3, 4, 9, 7, 1, 6]
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    display: false
                }
            },
            scales: {
                x: {
                    stacked: true,
                    ticks: {
                        callback: function (val, index) {
                            return index % 4 === 0 ? this.getLabelForValue(val as number) : '';
                        },
                        color: 'rgb(140, 142, 150)'
                    },
                    grid: {
                        color: 'rgba(140, 142, 150, 0.15)',
                        tickBorderDash: [8, 4]
                    }
                },
                y: {
                    stacked: true,
                    ticks: {
                        color: 'rgb(140, 142, 150)'
                    },
                    grid: {
                        color: 'rgba(140, 142, 150, 0.15)',
                        tickBorderDash: [8, 4]
                    }
                }
            }
        }
    });

    var sparklineData = [
        [300, 500, 220, 227, 231, 327, 119, 230, 321],
        [315, 319, 320, 322, 452, 610, 170, 427, 119, 430, 721],
        [5, 9, 10, 12, 23, 17, 21, 17, 9, 20, 11]
    ];

    sparklineData.forEach((data, idx) => {
        new Chart(document.getElementById('sparkline-' + (idx + 1)) as HTMLCanvasElement, {
            type: 'bar',
            data: {
                labels: data,
                datasets: [{
                    backgroundColor: "#fff",
                    data
                }]
            },
            options: {
                responsive: false,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: {
                        callbacks: {
                            title: s => null
                        },
                        displayColors: false,
                        yAlign: 'center'
                    }
                },
                scales: {
                    x: { display: false },
                    y: { display: false }
                }
            }
        });
    });

    document.querySelectorAll("canvas.traffic-knob").forEach(el => {
        var value = parseInt((el as HTMLElement).previousElementSibling.textContent, 10);
        new Chart(el as HTMLCanvasElement, {
            type: 'doughnut',
            data: {
                labels: ["", ""],
                datasets: [{
                    data: [value, 100 - value],
                    backgroundColor: [
                        '#39a0ff',
                        '#39a0ff50'
                    ],
                    weight: 0.5
                }]
            },
            options: {
                cutout: '65%',
                responsive: false,
                maintainAspectRatio: false,
                borderColor: 'transparent',
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: null
                }
            }
        });
    });

    var todoList = document.querySelector(".s-todo-list");
    if (todoList) {
        $(todoList).sortable({
            placeholder: "sort-highlight",
            handle: ".handle",
            forcePlaceholderSize: true,
            zIndex: 999999
        });

        todoList.addEventListener('change', function (e) {
            var li = (e.target as HTMLElement).closest('li');
            li && li.classList.toggle('s-todo-done');
        });

        $(todoList).closest('.card').find('.card-footer button').click((e) => {
            var input = $(e.target).closest('.input-group').find('input');
            var text = input.val().trim();
            if (text && text.length) {
                $(`<li>
                        <span class="handle">
                            <i class="fa fa-grip-vertical"></i>
                        </span>
                        <input type="checkbox" class="form-check-input" value="" name="">
                        <span class="text">${htmlEncode(text)}</span>
                        <small class="label"><i class="fa fa-clock-o"></i> ${(Math.trunc(Math.random() * 10) + 1) * 5} mins</small>
                    </li>`).appendTo(todoList);
                input.val('');
                input.focus();
            }
        });
    }

    $("#calendar").datepicker({
        showButtonPanel: false
    });
}
