<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch, ref } from 'vue'
import * as echarts from 'echarts'
const props = defineProps<{ points: any[], field: 'cpu' | 'memory', color: string }>()
const el = ref<HTMLElement>(); let chart: echarts.ECharts | undefined
const draw = () => {
  if (!chart || !props.points) return
  chart.setOption({ animationDuration: 500, grid: { left: 0, right: 2, top: 12, bottom: 0, containLabel: false }, xAxis: { type: 'category', show: false, data: props.points.map(p => new Date(p.time).toLocaleTimeString()) }, yAxis: { type: 'value', min: 0, max: 100, show: false }, tooltip: { trigger: 'axis', formatter: (x: any) => `${x[0].name}<br/><b>${x[0].value.toFixed(1)}%</b>` }, series: [{ type: 'line', data: props.points.map(p => p[props.field]), smooth: .35, symbol: 'none', lineStyle: { color: props.color, width: 2 }, areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: `${props.color}66` }, { offset: 1, color: `${props.color}05` }]) } }] })
}
onMounted(() => { chart = echarts.init(el.value!); draw(); addEventListener('resize', () => chart?.resize()) })
watch(() => props.points, draw, { deep: true }); onBeforeUnmount(() => chart?.dispose())
</script>
<template><div ref="el" class="metric-chart"></div></template>
